using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Reviews;

/// <summary>
/// Sprint 34 / B6 — concrete <see cref="IReviewWorkflow"/>. Persists
/// AnalystReview + Findings rows, opens / closes Sprint 31 SLA windows
/// keyed on the review type, and emits the
/// <c>nickerp.inspection.review.*</c> audit events the throughput
/// dashboard reads.
///
/// <para>
/// Vendor-neutral. The implementation only touches the InspectionDbContext
/// and the <see cref="ISlaTracker"/> contract — nothing in this file
/// references CMR, Fyco, BOE, regime codes, or any specific authority.
/// Adapter modules are free to drive their own per-authority review
/// shape on top of this without modifying core domain.
/// </para>
/// </summary>
public sealed class ReviewWorkflow : IReviewWorkflow
{
    /// <summary>
    /// Window name format for SLA tracking. Matches the convention
    /// in <c>SlaTracker.OpenToValidated</c>: dotted-lowercase. The
    /// type segment is the lower-cased <see cref="ReviewType"/>.
    /// </summary>
    public const string SlaWindowPrefix = "review.";

    private readonly InspectionDbContext _db;
    private readonly IEventPublisher _events;
    private readonly ITenantContext _tenant;
    private readonly ISlaTracker? _sla;
    private readonly ILogger<ReviewWorkflow> _logger;

    public ReviewWorkflow(
        InspectionDbContext db,
        IEventPublisher events,
        ITenantContext tenant,
        ILogger<ReviewWorkflow> logger,
        ISlaTracker? sla = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sla = sla;
    }

    /// <inheritdoc/>
    public async Task<Guid> StartReviewAsync(
        Guid caseId,
        ReviewType reviewType,
        Guid userId,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;

        var @case = await _db.Cases.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == caseId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Cannot start review on case {caseId} — not found in this tenant.");

        // A case may already have an in-progress session for this
        // analyst (Standard review path). Reuse it; otherwise open
        // one fresh — supervisors who never got the AssignSelf shortcut
        // still need a session for the AnalystReview FK.
        var session = await _db.ReviewSessions
            .Where(s => s.CaseId == caseId && s.AnalystUserId == userId && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (session is null)
        {
            session = new ReviewSession
            {
                Id = Guid.NewGuid(),
                CaseId = caseId,
                AnalystUserId = userId,
                StartedAt = now,
                Outcome = "in-progress",
                TenantId = tenantId,
            };
            _db.ReviewSessions.Add(session);
        }

        var review = new AnalystReview
        {
            Id = Guid.NewGuid(),
            ReviewSessionId = session.Id,
            ReviewType = reviewType,
            CreatedAt = now,
            StartedByUserId = userId,
            ConfidenceScore = 0.0,  // populated on Complete
            TenantId = tenantId,
        };
        _db.AnalystReviews.Add(review);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Sprint 31 integration — open the review-elapsed SLA window.
        // Best-effort: tracker absence shouldn't block the review.
        if (_sla is not null)
        {
            try
            {
                await _sla.OpenWindowsAsync(
                    caseId,
                    new[] { WindowNameFor(reviewType) },
                    now,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "SlaTracker.OpenWindowsAsync failed for case {CaseId} review {ReviewType}; throughput dashboard may under-count.",
                    caseId, reviewType);
            }
        }

        await EmitAsync(
            tenantId, userId, @case.CorrelationId,
            "nickerp.inspection.review.started",
            "AnalystReview",
            review.Id.ToString(),
            new
            {
                ReviewId = review.Id,
                CaseId = caseId,
                ReviewType = reviewType.ToString(),
                ReviewSessionId = session.Id,
            }, ct).ConfigureAwait(false);

        return review.Id;
    }

    /// <inheritdoc/>
    public async Task CompleteReviewAsync(
        Guid reviewId,
        string outcome,
        IReadOnlyList<Finding> findings,
        Guid userId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(outcome))
            throw new ArgumentException("Outcome cannot be empty.", nameof(outcome));
        EnsureTenantResolved();
        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;

        var review = await _db.AnalystReviews
            .FirstOrDefaultAsync(r => r.Id == reviewId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Cannot complete review {reviewId} — not found in this tenant.");
        if (review.CompletedAt is not null)
        {
            _logger.LogDebug(
                "Review {ReviewId} already completed at {CompletedAt}; ignoring CompleteReviewAsync.",
                reviewId, review.CompletedAt);
            return;
        }

        review.Outcome = outcome.Trim();
        review.CompletedAt = now;
        if (review.TimeToDecisionMs == 0)
        {
            // Estimate elapsed if not set — typical when the page goes
            // through Start → Complete without recording per-region dwell.
            review.TimeToDecisionMs = (int)Math.Min(
                int.MaxValue, (now - review.CreatedAt).TotalMilliseconds);
        }

        // Persist the analyst's findings against this review. Caller-
        // supplied AnalystReviewId is ignored (defensive) so a typo
        // doesn't strand findings on a different review.
        if (findings is not null)
        {
            foreach (var f in findings)
            {
                if (f is null) continue;
                f.Id = f.Id == Guid.Empty ? Guid.NewGuid() : f.Id;
                f.AnalystReviewId = review.Id;
                f.TenantId = tenantId;
                if (f.CreatedAt == default) f.CreatedAt = now;
                _db.Findings.Add(f);
            }
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Look up the case id for the SLA-window close + the audit
        // event payload.
        var session = await _db.ReviewSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == review.ReviewSessionId, ct).ConfigureAwait(false);
        var caseId = session?.CaseId ?? Guid.Empty;
        if (_sla is not null && caseId != Guid.Empty)
        {
            try
            {
                await _sla.CloseWindowAsync(
                    caseId, WindowNameFor(review.ReviewType), now, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "SlaTracker.CloseWindowAsync failed for case {CaseId} review {ReviewType}; window may stay open.",
                    caseId, review.ReviewType);
            }
        }

        await EmitAsync(
            tenantId, userId, correlationId: null,
            "nickerp.inspection.review.completed",
            "AnalystReview",
            review.Id.ToString(),
            new
            {
                ReviewId = review.Id,
                CaseId = caseId,
                ReviewType = review.ReviewType.ToString(),
                Outcome = review.Outcome,
                FindingCount = findings?.Count ?? 0,
            }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task EscalateReviewAsync(
        Guid reviewId,
        Guid fromUserId,
        Guid toUserId,
        string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason cannot be empty.", nameof(reason));
        EnsureTenantResolved();
        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;

        var review = await _db.AnalystReviews
            .FirstOrDefaultAsync(r => r.Id == reviewId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Cannot escalate review {reviewId} — not found in this tenant.");
        if (review.CompletedAt is not null)
            throw new InvalidOperationException(
                $"Cannot escalate review {reviewId} — already completed at {review.CompletedAt}.");

        // The escalation re-points the StartedByUserId to the receiver
        // so /reviews/queue lights up for the supervisor.
        review.StartedByUserId = toUserId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Look up the case id for audit.
        var session = await _db.ReviewSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == review.ReviewSessionId, ct).ConfigureAwait(false);
        var caseId = session?.CaseId ?? Guid.Empty;

        await EmitAsync(
            tenantId, fromUserId, correlationId: null,
            "nickerp.inspection.review.escalated",
            "AnalystReview",
            review.Id.ToString(),
            new
            {
                ReviewId = review.Id,
                CaseId = caseId,
                ReviewType = review.ReviewType.ToString(),
                FromUserId = fromUserId,
                ToUserId = toUserId,
                Reason = reason.Trim(),
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the SLA-window name for a given review type. Vendor-neutral
    /// dotted-lowercase format mirroring
    /// <c>SlaTracker.OpenToValidated</c>'s convention.
    /// </summary>
    public static string WindowNameFor(ReviewType type) =>
        SlaWindowPrefix + type.ToString().ToLowerInvariant() + ".elapsed";

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; ReviewWorkflow must run inside a tenant-aware request scope.");
    }

    private async Task EmitAsync(
        long tenantId, Guid? actor, string? correlationId,
        string eventType, string entityType, string entityId, object payload,
        CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(
                tenantId, eventType, entityType, entityId, DateTimeOffset.UtcNow);
            var evt = DomainEvent.Create(
                tenantId, actor, correlationId, eventType, entityType, entityId, json, key);
            await _events.PublishAsync(evt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit emission must never break the user-facing flow.
            _logger.LogWarning(ex,
                "Failed to emit DomainEvent {EventType} for {EntityType} {EntityId}.",
                eventType, entityType, entityId);
        }
    }
}
