using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// User membership in an <see cref="AnalysisService"/> (VP6, locked
/// 2026-05-02). Permissions flow from service membership: a user sees
/// cases that fall within their service's location scope, filtered
/// further by <see cref="Tenant.CaseVisibilityModel"/>.
///
/// <para>
/// <see cref="Tenant.AllowMultiServiceMembership"/> controls whether a
/// user can join more than one service. When <c>false</c>, the
/// service-layer guard rejects a second membership; when <c>true</c>,
/// the user's queue is the union of cases visible to all their
/// services.
/// </para>
///
/// <para>
/// Composite primary key <c>(AnalysisServiceId, UserId)</c>.
/// <see cref="TenantId"/> is denormalised for RLS filtering.
/// </para>
/// </summary>
public sealed class AnalysisServiceUser : ITenantOwned
{
    public Guid AnalysisServiceId { get; set; }
    public AnalysisService? AnalysisService { get; set; }

    /// <summary>Identity user id (Guid) — references <c>identity.users</c> in the platform DB; no FK across DBs.</summary>
    public Guid UserId { get; set; }

    public DateTimeOffset AssignedAt { get; set; }

    /// <summary>Admin who assigned this membership. Null for the bootstrap "All Locations" auto-grant.</summary>
    public Guid? AssignedByUserId { get; set; }

    public long TenantId { get; set; }
}
