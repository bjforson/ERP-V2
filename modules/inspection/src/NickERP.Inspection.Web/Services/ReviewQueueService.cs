using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.AnalysisServices;
using NickERP.Inspection.Application.Reviews;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 34 / B6 Phase C — analyst-facing read + claim service for the
/// specialised-review pages. Composes
/// <see cref="CaseVisibilityService"/> (which cases are reachable
/// through the user's <see cref="AnalysisServiceUser"/> memberships),
/// <see cref="CaseClaimService"/> (first-claim-wins under shared
/// visibility), and <see cref="IReviewWorkflow"/> (open the
/// AnalystReview row + audit-trailed open/close).
///
/// <para>
/// Vendor-neutral. The throughput rollup at the bottom of this file
/// reads <c>analyst_reviews</c> grouped by <see cref="ReviewType"/>;
/// the queue list reads <c>cases</c> ordered by
/// <see cref="ReviewQueue"/> + <see cref="InspectionCase.OpenedAt"/>.
/// No Ghana port codes / regime codes / specific commodity types.
/// </para>
///
/// <para>
/// All methods assume the request has a resolved tenant + RLS narrowing
/// in place. <see cref="ClaimReviewAsync"/> wraps the unique-violation
/// race that <see cref="CaseClaimService"/> already handles; callers
/// see <see cref="CaseAlreadyClaimedException"/> on lost-race.
/// </para>
/// </summary>
public sealed class ReviewQueueService
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly CaseVisibilityService _visibility;
    private readonly CaseClaimService _claims;
    private readonly IReviewWorkflow _reviews;
    private readonly ILogger<ReviewQueueService> _logger;

    public ReviewQueueService(
        InspectionDbContext db,
        ITenantContext tenant,
        CaseVisibilityService visibility,
        CaseClaimService claims,
        IReviewWorkflow reviews,
        ILogger<ReviewQueueService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        _claims = claims ?? throw new ArgumentNullException(nameof(claims));
        _reviews = reviews ?? throw new ArgumentNullException(nameof(reviews));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// List the user's review queue: cases reachable through
    /// <see cref="CaseVisibilityService"/> filtered by an optional
    /// <paramref name="queueFilter"/>. Ordered by
    /// <see cref="ReviewQueue"/> DESC then OpenedAt ASC (oldest urgent
    /// first), then takes <paramref name="take"/>.
    /// </summary>
    public async Task<IReadOnlyList<ReviewQueueRow>> GetMyQueueAsync(
        Guid userId,
        ReviewQueue? queueFilter = null,
        int take = 50,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved) return Array.Empty<ReviewQueueRow>();

        // Pull the user's visible cases through the existing visibility
        // service so shared/exclusive routing rules are honoured.
        var cases = await _visibility.GetVisibleCasesAsync(
            userId, take: take * 2, ct: ct).ConfigureAwait(false);
        if (cases.Count == 0) return Array.Empty<ReviewQueueRow>();

        if (queueFilter is { } q)
            cases = cases.Where(c => c.ReviewQueue == q).ToList();

        // Order by priority bucket then OpenedAt ASC (oldest first); take.
        var ordered = cases
            .OrderByDescending(c => (int)c.ReviewQueue)
            .ThenBy(c => c.OpenedAt)
            .Take(take)
            .ToList();
        if (ordered.Count == 0) return Array.Empty<ReviewQueueRow>();

        // Hydrate active-claim metadata in one pass so we can flag
        // already-claimed-by-other rows on the queue.
        var caseIds = ordered.Select(c => c.Id).ToList();
        var activeClaims = await _db.CaseClaims.AsNoTracking()
            .Include(cc => cc.AnalysisService)
            .Where(cc => caseIds.Contains(cc.CaseId) && cc.ReleasedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        var claimByCase = activeClaims.ToDictionary(cc => cc.CaseId, cc => cc);

        return ordered.Select(c => new ReviewQueueRow(
            CaseId: c.Id,
            SubjectIdentifier: c.SubjectIdentifier,
            State: c.State,
            Queue: c.ReviewQueue,
            OpenedAt: c.OpenedAt,
            ActiveClaim: claimByCase.GetValueOrDefault(c.Id))).ToList();
    }

    /// <summary>
    /// Acquire the case claim and start a review of
    /// <paramref name="reviewType"/> for <paramref name="userId"/>.
    /// First-claim-wins: under shared visibility the claim acquire is
    /// the same unique-partial-index race
    /// <see cref="CaseClaimService.AcquireClaimAsync"/> handles —
    /// concurrent calls land at most one winner, the loser sees
    /// <see cref="CaseAlreadyClaimedException"/>.
    /// </summary>
    /// <returns>The new AnalystReview row id.</returns>
    public async Task<Guid> ClaimReviewAsync(
        Guid caseId,
        Guid analysisServiceId,
        ReviewType reviewType,
        Guid userId,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; ReviewQueueService cannot claim outside a tenant scope.");

        // Acquire the visibility claim first — surfaces a benign
        // CaseAlreadyClaimedException to the page if the loser race-d.
        await _claims.AcquireClaimAsync(caseId, analysisServiceId, userId, ct).ConfigureAwait(false);
        // Then open the typed AnalystReview row + audit event.
        return await _reviews.StartReviewAsync(caseId, reviewType, userId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hand a review off to a different user (typically a supervisor).
    /// Audit-trailed via <c>nickerp.inspection.review.escalated</c>.
    /// </summary>
    public Task EscalateReviewAsync(
        Guid reviewId,
        Guid fromUserId,
        Guid toUserId,
        string reason,
        CancellationToken ct = default)
        => _reviews.EscalateReviewAsync(reviewId, fromUserId, toUserId, reason, ct);

    /// <summary>
    /// Throughput rollup for the admin throughput page (or an embedded
    /// card on /admin/reports). Counts AnalystReview rows in
    /// <paramref name="window"/>, grouped by <see cref="ReviewType"/>,
    /// split by completed vs in-progress.
    /// </summary>
    public async Task<ReviewThroughputSnapshot> GetThroughputAsync(
        TimeSpan window,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved) return ReviewThroughputSnapshot.Empty;

        var since = DateTimeOffset.UtcNow.Subtract(window);
        var rows = await _db.AnalystReviews.AsNoTracking()
            .Where(r => r.CreatedAt >= since)
            .GroupBy(r => r.ReviewType)
            .Select(g => new
            {
                Type = g.Key,
                Count = g.Count(),
                Completed = g.Count(r => r.CompletedAt != null),
                AvgMs = g.Where(r => r.CompletedAt != null)
                    .Select(r => (double?)r.TimeToDecisionMs)
                    .Average() ?? 0.0,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var byType = rows.ToDictionary(
            r => r.Type,
            r => new ReviewThroughputBucket(
                Total: r.Count,
                Completed: r.Completed,
                InProgress: r.Count - r.Completed,
                AverageTimeToDecisionMs: r.AvgMs));
        return new ReviewThroughputSnapshot(window, since, byType);
    }
}

/// <summary>
/// One row on the analyst's <c>/reviews/queue</c> page. Surfaces the
/// case identity, the priority bucket, and the active claim (when
/// another user has the case).
/// </summary>
public sealed record ReviewQueueRow(
    Guid CaseId,
    string SubjectIdentifier,
    InspectionWorkflowState State,
    ReviewQueue Queue,
    DateTimeOffset OpenedAt,
    CaseClaim? ActiveClaim);

/// <summary>
/// Throughput rollup for the admin Reviews-throughput page.
/// </summary>
public sealed record ReviewThroughputSnapshot(
    TimeSpan Window,
    DateTimeOffset Since,
    IReadOnlyDictionary<ReviewType, ReviewThroughputBucket> ByType)
{
    public static readonly ReviewThroughputSnapshot Empty =
        new(TimeSpan.Zero, DateTimeOffset.MinValue,
            new Dictionary<ReviewType, ReviewThroughputBucket>());
}

/// <summary>One throughput bucket — one ReviewType.</summary>
public sealed record ReviewThroughputBucket(
    int Total,
    int Completed,
    int InProgress,
    double AverageTimeToDecisionMs);
