using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.ExternalSystems;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Applies completed audit-review outcomes to case state, verdicts, and
/// outbound-submission queue handoff without depending on Blazor actor state.
/// </summary>
public sealed class AuditDispositionService : IAuditDispositionService
{
    private const string RoutedEventType = "inspection.audit_review.routed";

    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly ITransactionalQueue<OutboundSubmissionPayload> _submissionQueue;
    private readonly ExternalSystemAdminService _externalSystems;
    private readonly ILogger<AuditDispositionService> _logger;

    public AuditDispositionService(
        InspectionDbContext db,
        ITenantContext tenant,
        IEventPublisher events,
        ITransactionalQueue<OutboundSubmissionPayload> submissionQueue,
        ExternalSystemAdminService externalSystems,
        ILogger<AuditDispositionService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _submissionQueue = submissionQueue ?? throw new ArgumentNullException(nameof(submissionQueue));
        _externalSystems = externalSystems ?? throw new ArgumentNullException(nameof(externalSystems));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AuditDispositionResult> RouteAsync(
        AuditReviewPayload payload,
        Guid workItemId,
        string? correlationId,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "AuditDispositionService cannot run without a resolved tenant context.");
        }

        AuditDispositionResult result;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            result = await RouteCoreAsync(payload, workItemId, correlationId, ct)
                .ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await EmitRoutedEventAsync(workItemId, result, correlationId, ct)
            .ConfigureAwait(false);

        return result;
    }

    private async Task<AuditDispositionResult> RouteCoreAsync(
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

        var selected = await SelectAuditReviewAsync(payload, ct).ConfigureAwait(false);
        if (selected.Review.CompletedAt is null
            && selected.Review.StartedByUserId != Guid.Empty
            && selected.Session.AnalystUserId != Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Audit review {selected.Review.Id} for case {caseId} is not completed yet.");
        }

        if (selected.Review.CompletedAt is null)
        {
            CompleteSystemFallbackReview(selected.Review, selected.Session, now);
        }

        var outcome = NormalizeOutcome(selected.Review.Outcome);
        if (outcome is "dissent" or "hold" or "escalated")
        {
            @case.State = InspectionWorkflowState.Reviewed;
            @case.StateEnteredAt = now;
            @case.ReviewQueue = ReviewQueue.Exception;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new AuditDispositionResult(caseId, selected.Review.Id, outcome, null, false);
        }

        var decision = DecideVerdict(outcome, await LoadCaseFindingsAsync(caseId, ct).ConfigureAwait(false));
        var verdict = await EnsureVerdictAsync(@case.Id, selected, outcome, decision, now, ct)
            .ConfigureAwait(false);

        @case.State = InspectionWorkflowState.Verdict;
        @case.StateEnteredAt = now;

        var submission = await EnsureOutboundSubmissionAsync(@case, verdict, now, ct)
            .ConfigureAwait(false);
        var shouldEnqueueSubmission = submission.Status is "queued" or "pending" or "error";
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
        return new AuditDispositionResult(caseId, selected.Review.Id, outcome, decision, shouldEnqueueSubmission);
    }

    private async Task<AuditReviewSelection> SelectAuditReviewAsync(
        AuditReviewPayload payload,
        CancellationToken ct)
    {
        var caseId = payload.CaseId;
        var auditRows = await (
                from review in _db.AnalystReviews
                join session in _db.ReviewSessions on review.ReviewSessionId equals session.Id
                where session.CaseId == caseId && review.ReviewType == ReviewType.AuditReview
                orderby review.CompletedAt descending, review.CreatedAt descending
                select new AuditReviewSelection(review, session))
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

        return selected
            ?? throw new InvalidOperationException(
                $"Case {caseId} has no completed audit review and no system-fallback audit review to route.");
    }

    private void CompleteSystemFallbackReview(
        AnalystReview review,
        ReviewSession session,
        DateTimeOffset now)
    {
        review.Outcome = "concur";
        review.CompletedAt = now;
        review.TimeToDecisionMs = (int)Math.Min(
            int.MaxValue,
            (now - review.CreatedAt).TotalMilliseconds);
        session.EndedAt = now;
        session.Outcome = "completed";
        _db.Findings.Add(new Finding
        {
            Id = Guid.NewGuid(),
            AnalystReviewId = review.Id,
            FindingType = "review.audit.system_auto_concur",
            Severity = "info",
            LocationInImageJson = "{}",
            Note = "System fallback auto-concurred because no analysis-service user was available.",
            CreatedAt = now,
            TenantId = _tenant.TenantId
        });
    }

    private async Task<Verdict> EnsureVerdictAsync(
        Guid caseId,
        AuditReviewSelection selected,
        string outcome,
        VerdictDecision decision,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var verdict = await _db.Verdicts
            .FirstOrDefaultAsync(v => v.CaseId == caseId, ct)
            .ConfigureAwait(false);
        if (verdict is not null)
        {
            return verdict;
        }

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
        return verdict;
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
            Status = "queued",
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
        var instance = await _externalSystems
            .ResolvePreferredServingInstanceAsync(locationId, ct)
            .ConfigureAwait(false);
        return instance
            ?? throw new InvalidOperationException(
                $"No active ExternalSystemInstance serves location {locationId}; cannot enqueue outbound submission.");
    }

    private async Task EmitRoutedEventAsync(
        Guid workItemId,
        AuditDispositionResult result,
        string? correlationId,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                caseId = result.CaseId,
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
                result.CaseId,
                result.ReviewId);
            var evt = DomainEvent.Create(
                _tenant.TenantId,
                actorUserId: null,
                correlationId: correlationId,
                eventType: RoutedEventType,
                entityType: nameof(InspectionCase),
                entityId: result.CaseId.ToString(),
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
                result.CaseId);
        }
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

    private sealed record AuditReviewSelection(AnalystReview Review, ReviewSession Session);
}
