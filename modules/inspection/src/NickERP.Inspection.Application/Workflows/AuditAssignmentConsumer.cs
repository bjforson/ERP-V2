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
/// Consumer for <c>inspection.queue_audit_assignment</c>. Assigns a
/// case to an eligible analysis-service user and opens an audit review.
/// System fallback assignments are also queued for automatic audit review.
/// </summary>
public sealed class AuditAssignmentConsumer : IQueueConsumer<AuditAssignmentPayload>
{
    private const string EventType = "inspection.audit_assignment.assigned";

    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly ITransactionalQueue<AuditReviewPayload> _auditReviewQueue;
    private readonly ILogger<AuditAssignmentConsumer> _logger;

    public AuditAssignmentConsumer(
        InspectionDbContext db,
        ITenantContext tenant,
        IEventPublisher events,
        ITransactionalQueue<AuditReviewPayload> auditReviewQueue,
        ILogger<AuditAssignmentConsumer> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _auditReviewQueue = auditReviewQueue ?? throw new ArgumentNullException(nameof(auditReviewQueue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ProcessAsync(IQueueClaim<AuditAssignmentPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "AuditAssignmentConsumer cannot run without a resolved tenant context.");
        }

        var caseId = claim.Payload.CaseId;
        AuditAssignmentResult result;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            result = await AssignAsync(caseId, ct).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            if (result.SystemFallback)
            {
                await _auditReviewQueue.EnqueueAsync(
                        _db,
                        new EnqueueRequest<AuditReviewPayload>
                        {
                            WorkItemId = claim.WorkItemId,
                            Payload = new AuditReviewPayload(claim.WorkItemId, caseId, now),
                            IdempotencyKey = IdempotencyKey.From(
                                "inspection",
                                "audit-review",
                                claim.WorkItemId,
                                caseId),
                            CorrelationId = claim.CorrelationId
                        },
                        ct)
                    .ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await EmitAssignedEventAsync(caseId, claim.WorkItemId, result, claim.CorrelationId, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Audit assignment completed for CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount}; AssignedUserId={AssignedUserId} AnalysisServiceId={AnalysisServiceId} ReviewId={ReviewId}; EnqueuedAuditReview={EnqueuedAuditReview}",
            caseId,
            claim.WorkItemId,
            claim.AttemptCount,
            result.AssignedUserId,
            result.AnalysisServiceId,
            result.ReviewId,
            result.SystemFallback);
    }

    private async Task<AuditAssignmentResult> AssignAsync(Guid caseId, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;

        var @case = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");

        var activeClaim = await _db.CaseClaims
            .FirstOrDefaultAsync(c => c.CaseId == caseId && c.ReleasedAt == null, ct)
            .ConfigureAwait(false);

        AssignmentCandidate candidate;
        if (activeClaim is not null)
        {
            candidate = new AssignmentCandidate(
                activeClaim.AnalysisServiceId,
                activeClaim.ClaimedByUserId,
                IsSystemFallback: activeClaim.ClaimedByUserId == Guid.Empty);
        }
        else
        {
            candidate = await ChooseCandidateAsync(@case.LocationId, ct).ConfigureAwait(false);
            activeClaim = new CaseClaim
            {
                Id = Guid.NewGuid(),
                CaseId = caseId,
                AnalysisServiceId = candidate.AnalysisServiceId,
                ClaimedByUserId = candidate.UserId,
                ClaimedAt = now,
                TenantId = tenantId
            };
            _db.CaseClaims.Add(activeClaim);
        }

        @case.AssignedAnalystUserId = candidate.UserId;
        if (@case.State != InspectionWorkflowState.Assigned)
        {
            @case.State = InspectionWorkflowState.Assigned;
            @case.StateEnteredAt = now;
        }

        var session = await _db.ReviewSessions
            .Where(s => s.CaseId == caseId
                     && s.AnalystUserId == candidate.UserId
                     && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (session is null)
        {
            session = new ReviewSession
            {
                Id = Guid.NewGuid(),
                CaseId = caseId,
                AnalystUserId = candidate.UserId,
                StartedAt = now,
                Outcome = "in-progress",
                TenantId = tenantId
            };
            _db.ReviewSessions.Add(session);
        }

        var review = await _db.AnalystReviews
            .Where(r => r.ReviewSessionId == session.Id
                     && r.ReviewType == ReviewType.AuditReview
                     && r.CompletedAt == null)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (review is null)
        {
            review = new AnalystReview
            {
                Id = Guid.NewGuid(),
                ReviewSessionId = session.Id,
                ReviewType = ReviewType.AuditReview,
                CreatedAt = now,
                StartedByUserId = candidate.UserId,
                ConfidenceScore = 0.0,
                TenantId = tenantId
            };
            _db.AnalystReviews.Add(review);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new AuditAssignmentResult(
            candidate.AnalysisServiceId,
            candidate.UserId,
            session.Id,
            review.Id,
            candidate.IsSystemFallback);
    }

    private async Task<AssignmentCandidate> ChooseCandidateAsync(Guid locationId, CancellationToken ct)
    {
        var serviceIds = await _db.AnalysisServiceLocations.AsNoTracking()
            .Where(l => l.LocationId == locationId)
            .Select(l => l.AnalysisServiceId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (serviceIds.Count == 0)
        {
            throw new InvalidOperationException(
                $"No AnalysisService covers location {locationId}; cannot assign audit review.");
        }

        var serviceLocationCounts = await _db.AnalysisServiceLocations.AsNoTracking()
            .Where(l => serviceIds.Contains(l.AnalysisServiceId))
            .GroupBy(l => l.AnalysisServiceId)
            .Select(g => new { ServiceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ServiceId, x => x.Count, ct)
            .ConfigureAwait(false);

        var candidates = await _db.AnalysisServiceUsers.AsNoTracking()
            .Where(u => serviceIds.Contains(u.AnalysisServiceId))
            .Join(
                _db.AnalysisServices.AsNoTracking(),
                u => u.AnalysisServiceId,
                s => s.Id,
                (u, s) => new
                {
                    u.AnalysisServiceId,
                    u.UserId,
                    u.AssignedAt,
                    ServiceCreatedAt = s.CreatedAt
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var selected = candidates
            .OrderBy(c => serviceLocationCounts.TryGetValue(c.AnalysisServiceId, out var count) ? count : int.MaxValue)
            .ThenBy(c => c.ServiceCreatedAt)
            .ThenBy(c => c.AssignedAt)
            .ThenBy(c => c.UserId)
            .FirstOrDefault();
        if (selected is not null)
        {
            return new AssignmentCandidate(
                selected.AnalysisServiceId,
                selected.UserId,
                IsSystemFallback: false);
        }

        var fallbackServiceId = await _db.AnalysisServices.AsNoTracking()
            .Where(s => serviceIds.Contains(s.Id))
            .OrderByDescending(s => s.IsBuiltInAllLocations)
            .ThenBy(s => s.CreatedAt)
            .Select(s => s.Id)
            .FirstAsync(ct)
            .ConfigureAwait(false);

        return new AssignmentCandidate(
            fallbackServiceId,
            Guid.Empty,
            IsSystemFallback: true);
    }

    private async Task EmitAssignedEventAsync(
        Guid caseId,
        Guid workItemId,
        AuditAssignmentResult result,
        string? correlationId,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                caseId,
                workItemId,
                analysisServiceId = result.AnalysisServiceId,
                assignedUserId = result.AssignedUserId,
                reviewSessionId = result.ReviewSessionId,
                reviewId = result.ReviewId,
                systemFallback = result.SystemFallback
            });
            var key = IdempotencyKey.From(
                _tenant.TenantId,
                EventType,
                workItemId,
                caseId);
            var evt = DomainEvent.Create(
                _tenant.TenantId,
                actorUserId: null,
                correlationId: correlationId,
                eventType: EventType,
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
                EventType,
                caseId);
        }
    }

    private sealed record AssignmentCandidate(Guid AnalysisServiceId, Guid UserId, bool IsSystemFallback);

    private sealed record AuditAssignmentResult(
        Guid AnalysisServiceId,
        Guid AssignedUserId,
        Guid ReviewSessionId,
        Guid ReviewId,
        bool SystemFallback);
}
