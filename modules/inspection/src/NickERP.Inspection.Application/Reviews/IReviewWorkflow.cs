using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Application.Reviews;

/// <summary>
/// Sprint 34 / B6 — orchestrator for the specialised review pages
/// (BL / AI triage / supervisor audit). Lives in
/// <c>Application/Reviews</c> so it stays vendor-neutral; the v2
/// domain only knows about <see cref="ReviewType"/> + the persisted
/// audit-trailed row, not Ghana port codes / regime codes / specific
/// commodity types.
///
/// <para>
/// <b>Lifecycle.</b>
/// </para>
/// <list type="bullet">
///   <item><see cref="StartReviewAsync"/> — opens an
///   <see cref="AnalystReview"/> row keyed to the case + analyst, opens an
///   <c>review.{type}.elapsed</c> SLA window via Sprint 31's
///   <c>ISlaTracker</c> (best-effort; review proceeds if the tracker
///   is missing), and emits <c>nickerp.inspection.review.started</c>.</item>
///   <item><see cref="CompleteReviewAsync"/> — closes the row's
///   outcome + Findings, closes the SLA window, and emits
///   <c>nickerp.inspection.review.completed</c>.</item>
///   <item><see cref="EscalateReviewAsync"/> — re-targets a review
///   to a different user (supervisor escalation); audit-trailed via
///   <c>nickerp.inspection.review.escalated</c>.</item>
/// </list>
///
/// <para>
/// <b>Tenant context.</b> Every method assumes
/// <see cref="NickERP.Platform.Tenancy.ITenantContext"/> is resolved;
/// RLS narrows reads + writes through the
/// <c>tenant_isolation_analyst_reviews</c> + sibling policies. No
/// <c>SetSystemContext</c> calls — reviews always run in the user's
/// tenant scope.
/// </para>
/// </summary>
public interface IReviewWorkflow
{
    /// <summary>
    /// Open a review of <paramref name="reviewType"/> on the given case
    /// for <paramref name="userId"/>. Looks up the case's most-recent
    /// open <see cref="ReviewSession"/> for the analyst (creating one
    /// if absent — supervisor / fresh-page entry path), then attaches
    /// a fresh <see cref="AnalystReview"/> row.
    /// </summary>
    /// <returns>The new AnalystReview row id.</returns>
    Task<Guid> StartReviewAsync(
        Guid caseId,
        ReviewType reviewType,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Close a review with <paramref name="outcome"/> and persist the
    /// supplied findings. Each finding's
    /// <see cref="Finding.AnalystReviewId"/> is overwritten to the
    /// review's id (callers don't have to set it). Closes the matching
    /// SLA window if one is open.
    /// </summary>
    Task CompleteReviewAsync(
        Guid reviewId,
        string outcome,
        IReadOnlyList<Finding> findings,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Hand a review off to <paramref name="toUserId"/> (typically a
    /// supervisor). The audit event captures the from/to user ids and
    /// the supplied <paramref name="reason"/>. The review's
    /// AnalystReview row is left open; the receiving user is expected
    /// to call <see cref="CompleteReviewAsync"/> when done.
    /// </summary>
    Task EscalateReviewAsync(
        Guid reviewId,
        Guid fromUserId,
        Guid toUserId,
        string reason,
        CancellationToken ct = default);
}
