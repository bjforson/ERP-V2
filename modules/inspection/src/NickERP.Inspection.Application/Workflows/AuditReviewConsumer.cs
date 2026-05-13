using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Consumer for <c>inspection.queue_audit_review</c>. Records the final
/// audit-review routing decision, writes the case verdict when the audit
/// outcome is terminal, and queues outbound submission.
/// </summary>
public sealed class AuditReviewConsumer : IQueueConsumer<AuditReviewPayload>
{
    private const string RoutedEventType = "inspection.audit_review.routed";

    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly ITransactionalQueue<OutboundSubmissionPayload> _submissionQueue;
    private readonly ILogger<AuditReviewConsumer> _logger;

    public AuditReviewConsumer(
        InspectionDbContext db,
        ITenantContext tenant,
        IEventPublisher events,
        ITransactionalQueue<OutboundSubmissionPayload> submissionQueue,
        ILogger<AuditReviewConsumer> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _submissionQueue = submissionQueue ?? throw new ArgumentNullException(nameof(submissionQueue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ProcessAsync(IQueueClaim<AuditReviewPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "AuditReviewConsumer cannot run without a resolved tenant context.");
        }

        var caseId = claim.Payload.CaseId;
        AuditReviewRouteResult result;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            result = await RouteAsync(claim.Payload, claim.WorkItemId, claim.CorrelationId, ct)
                .ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await EmitRoutedEventAsync(caseId, claim.WorkItemId, result, claim.CorrelationId, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Audit review routed for CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount}; Outcome={Outcome} VerdictDecision={VerdictDecision} EnqueuedSubmission={EnqueuedSubmission}",
            caseId,
            claim.WorkItemId,
            claim.AttemptCount,
            result.Outcome,
            result.VerdictDecision?.ToString() ?? "none",
            result.EnqueuedSubmission);
    }

    private async Task<AuditReviewRouteResult> RouteAsync(
        AuditReviewPayload payload,
        Guid workItemId,
        string? correlationId,
        CancellationToken ct)
    {
        var caseId = payload.CaseId;
        var now = DateTimeOffset.UtcNow;
        var @case = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");

        var auditRows = await (
                from review in _db.AnalystReviews
                join session in _db.ReviewSessions on review.ReviewSessionId equals session.Id
                where session.CaseId == caseId && review.ReviewType == ReviewType.AuditReview
                orderby review.CompletedAt descending, review.CreatedAt descending
                select new { Review = review, Session = session })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var selected = payload.ReviewId is { } reviewId
            ? auditRows.FirstOrDefault(r => r.Review.Id == reviewId)
                ?? throw new InvalidOperationException(
                    $"Audit review {reviewId} was not found for case {caseId}.")
            : auditRows.FirstOrDefault(r => r.Review.CompletedAt is not null)
                ?? auditRows.FirstOrDefault(r =>
                    r.Review.CompletedAt is null
                    && (r.Review.StartedByUserId == Guid.Empty
                        || r.Session.AnalystUserId == Guid.Empty));
        if (selected is null)
        {
            throw new InvalidOperationException(
                $"Case {caseId} has no completed audit review and no system-fallback audit review to route.");
        }

        if (selected.Review.CompletedAt is null
            && selected.Review.StartedByUserId != Guid.Empty
            && selected.Session.AnalystUserId != Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Audit review {selected.Review.Id} for case {caseId} is not completed yet.");
        }

        if (selected.Review.CompletedAt is null)
        {
            selected.Review.Outcome = "concur";
            selected.Review.CompletedAt = now;
            selected.Review.TimeToDecisionMs = (int)Math.Min(
                int.MaxValue,
                (now - selected.Review.CreatedAt).TotalMilliseconds);
            selected.Session.EndedAt = now;
            selected.Session.Outcome = "completed";
            _db.Findings.Add(new Finding
            {
                Id = Guid.NewGuid(),
                AnalystReviewId = selected.Review.Id,
                FindingType = "review.audit.system_auto_concur",
                Severity = "info",
                LocationInImageJson = "{}",
                Note = "System fallback auto-concurred because no analysis-service user was available.",
                CreatedAt = now,
                TenantId = _tenant.TenantId
            });
        }

        var outcome = NormalizeOutcome(selected.Review.Outcome);
        if (outcome is "hold" or "escalated")
        {
            @case.State = InspectionWorkflowState.Reviewed;
            @case.StateEnteredAt = now;
            @case.ReviewQueue = ReviewQueue.Exception;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new AuditReviewRouteResult(selected.Review.Id, outcome, null, EnqueuedSubmission: false);
        }

        var verdict = await _db.Verdicts
            .FirstOrDefaultAsync(v => v.CaseId == caseId, ct)
            .ConfigureAwait(false);
        var decision = DecideVerdict(outcome, await LoadCaseFindingsAsync(caseId, ct).ConfigureAwait(false));
        if (verdict is null)
        {
            verdict = new Verdict
            {
                Id = Guid.NewGuid(),
                CaseId = caseId,
                Decision = decision,
                Basis = BuildBasis(outcome, decision),
                DecidedAt = now,
                DecidedByUserId = selected.Review.StartedByUserId
                    ?? selected.Session.AnalystUserId,
                TenantId = _tenant.TenantId
            };
            _db.Verdicts.Add(verdict);
        }

        @case.State = InspectionWorkflowState.Verdict;
        @case.StateEnteredAt = now;

        var submission = await EnsureOutboundSubmissionAsync(@case, verdict, now, ct)
            .ConfigureAwait(false);
        var shouldEnqueueSubmission = submission.Status is "pending" or "error";

        if (shouldEnqueueSubmission)
        {
            await _submissionQueue.EnqueueAsync(
                _db,
                new EnqueueRequest<OutboundSubmissionPayload>
                {
                    WorkItemId = workItemId,
                    Payload = new OutboundSubmissionPayload(workItemId, caseId, now)
                    {
                        OutboundSubmissionId = submission.Id,
                        ExternalSystemInstanceId = submission.ExternalSystemInstanceId,
                        IdempotencyKey = submission.IdempotencyKey
                    },
                    IdempotencyKey = IdempotencyKey.From(
                        "inspection",
                        "submission",
                        workItemId,
                        caseId,
                        submission.Id),
                    CorrelationId = correlationId
                },
                ct).ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new AuditReviewRouteResult(
            selected.Review.Id,
            outcome,
            decision,
            EnqueuedSubmission: shouldEnqueueSubmission);
    }

    private async Task<List<Finding>> LoadCaseFindingsAsync(Guid caseId, CancellationToken ct)
    {
        return await (
                from finding in _db.Findings.AsNoTracking()
                join review in _db.AnalystReviews.AsNoTracking() on finding.AnalystReviewId equals review.Id
                join session in _db.ReviewSessions.AsNoTracking() on review.ReviewSessionId equals session.Id
                where session.CaseId == caseId
                select finding)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<OutboundSubmission> EnsureOutboundSubmissionAsync(
        InspectionCase @case,
        Verdict verdict,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var instance = await ResolveExternalSystemInstanceAsync(@case.LocationId, ct)
            .ConfigureAwait(false);
        var idempotencyKey = IdempotencyKey.From(
            _tenant.TenantId,
            "submission",
            @case.Id,
            verdict.Id,
            instance.Id);
        var existing = await _db.OutboundSubmissions
            .FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var payload = JsonSerializer.Serialize(new
        {
            caseId = @case.Id,
            decision = verdict.Decision.ToString(),
            basis = verdict.Basis
        });
        var submission = new OutboundSubmission
        {
            Id = Guid.NewGuid(),
            CaseId = @case.Id,
            ExternalSystemInstanceId = instance.Id,
            PayloadJson = payload,
            IdempotencyKey = idempotencyKey,
            Status = "pending",
            SubmittedAt = now,
            TenantId = _tenant.TenantId
        };
        _db.OutboundSubmissions.Add(submission);
        return submission;
    }

    private async Task<ExternalSystemInstance> ResolveExternalSystemInstanceAsync(
        Guid locationId,
        CancellationToken ct)
    {
        var shared = await _db.ExternalSystemInstances.AsNoTracking()
            .Where(e => e.IsActive && e.Scope == ExternalSystemBindingScope.Shared)
            .OrderBy(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (shared is not null)
        {
            return shared;
        }

        var bound = await _db.ExternalSystemBindings.AsNoTracking()
            .Where(b => b.LocationId == locationId
                        && b.Instance != null
                        && b.Instance.IsActive)
            .OrderBy(b => b.Role == "primary" ? 0 : 1)
            .ThenBy(b => b.CreatedAt)
            .Select(b => b.Instance!)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return bound
            ?? throw new InvalidOperationException(
                $"No active ExternalSystemInstance serves location {locationId}; cannot enqueue outbound submission.");
    }

    private static string NormalizeOutcome(string? outcome)
        => string.IsNullOrWhiteSpace(outcome)
            ? "concur"
            : outcome.Trim().ToLowerInvariant() switch
            {
                "complete" or "completed" or "confirmed" => "concur",
                "disagree" or "dissent" => "dissent",
                "escalate" => "escalated",
                var normalized => normalized
            };

    private static VerdictDecision DecideVerdict(string outcome, IReadOnlyCollection<Finding> findings)
    {
        if (outcome is "dissent")
        {
            return VerdictDecision.HoldForInspection;
        }

        var severities = findings
            .Select(f => f.Severity?.Trim().ToLowerInvariant())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return severities.Overlaps(new[] { "critical", "error", "warning", "partial", "incomplete" })
            ? VerdictDecision.HoldForInspection
            : VerdictDecision.Clear;
    }

    private static string BuildBasis(string outcome, VerdictDecision decision)
        => outcome == "dissent"
            ? "Audit review dissented from the prior analyst result; case held for inspection."
            : decision == VerdictDecision.Clear
                ? "Audit review concurred and no blocking findings remain."
                : "Audit review concurred with findings that require inspection.";

    private async Task EmitRoutedEventAsync(
        Guid caseId,
        Guid workItemId,
        AuditReviewRouteResult result,
        string? correlationId,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                caseId,
                workItemId,
                reviewId = result.ReviewId,
                outcome = result.Outcome,
                verdictDecision = result.VerdictDecision?.ToString(),
                enqueuedSubmission = result.EnqueuedSubmission
            });
            var key = IdempotencyKey.From(
                _tenant.TenantId,
                RoutedEventType,
                workItemId,
                caseId,
                result.ReviewId);
            var evt = DomainEvent.Create(
                _tenant.TenantId,
                actorUserId: null,
                correlationId: correlationId,
                eventType: RoutedEventType,
                entityType: nameof(InspectionCase),
                entityId: caseId.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to emit {EventType} for case {CaseId}.",
                RoutedEventType,
                caseId);
        }
    }

    private sealed record AuditReviewRouteResult(
        Guid ReviewId,
        string Outcome,
        VerdictDecision? VerdictDecision,
        bool EnqueuedSubmission);
}
