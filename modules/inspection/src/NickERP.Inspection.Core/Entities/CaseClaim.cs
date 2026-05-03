using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// First-claim-wins lock under shared visibility (VP6, locked
/// 2026-05-02). When two or more <see cref="AnalysisService"/>s can see
/// the same <see cref="InspectionCase"/> (because the tenant chose
/// <see cref="CaseVisibilityModel.Shared"/>), the first analyst to open
/// the case acquires a <see cref="CaseClaim"/>. Other services display
/// "claimed by [user] in [service]" and cannot work it until the claim
/// is released.
///
/// <para>
/// **Concurrency:** a unique partial index on
/// <c>(CaseId) WHERE ReleasedAt IS NULL</c> enforces at-most-one-active-claim
/// per case. Concurrent acquires race on the unique violation: winner
/// commits, loser's INSERT throws and the service surfaces the existing
/// claim's metadata for the UI's badge.
/// </para>
///
/// <para>
/// **Release:** the claim owner (or admin) sets
/// <see cref="ReleasedAt"/>; once released, another service can
/// re-acquire. The row is preserved for audit trail.
/// </para>
///
/// <para>
/// Under <see cref="CaseVisibilityModel.Exclusive"/> mode, claim
/// acquisition is unnecessary because exactly one service ever sees a
/// given case. The table still exists and is used; rows under exclusive
/// mode just never race.
/// </para>
/// </summary>
public sealed class CaseClaim : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }
    public InspectionCase? Case { get; set; }

    public Guid AnalysisServiceId { get; set; }
    public AnalysisService? AnalysisService { get; set; }

    public Guid ClaimedByUserId { get; set; }

    public DateTimeOffset ClaimedAt { get; set; }

    /// <summary>Set when the analyst closes / abandons the case. Null while active.</summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>User who released the claim — usually equals <see cref="ClaimedByUserId"/>; admin override is allowed.</summary>
    public Guid? ReleasedByUserId { get; set; }

    public long TenantId { get; set; }
}
