using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// VP6 (locked 2026-05-02) — image-analysis is organised into one or more
/// <see cref="AnalysisService"/>s per tenant. Each service has a scope of
/// one or more locations (location-scoped or federation-scoped — same
/// entity shape, different cardinality of owned locations).
///
/// <para>
/// **N:N location ↔ service.** A location can belong to multiple services
/// via <see cref="AnalysisServiceLocation"/>. Users join services via
/// <see cref="AnalysisServiceUser"/>; permissions flow from membership.
/// </para>
///
/// <para>
/// **Built-in default:** every tenant has exactly one immutable,
/// un-deletable <see cref="AnalysisService"/> with
/// <see cref="IsBuiltInAllLocations"/> = <c>true</c>. Every location
/// auto-joins it at creation. Admins can grant or revoke analyst access
/// to it but cannot delete the service itself. Unrouted cases are
/// impossible by construction. A unique partial index on
/// <c>(TenantId) WHERE IsBuiltInAllLocations</c> enforces at most one
/// per tenant.
/// </para>
///
/// <para>
/// **Tenant-configurable choices** live on <see cref="Tenant"/>:
/// <see cref="Tenant.CaseVisibilityModel"/> picks shared vs exclusive
/// case routing, and <see cref="Tenant.AllowMultiServiceMembership"/>
/// picks whether a user can join more than one service.
/// </para>
///
/// <para>
/// **First-claim-wins** under shared visibility: the first analyst to
/// open a case locks it via <see cref="CaseClaim"/>; other services
/// display "claimed by [user] in [service]" and cannot work it. The
/// unique partial index on <c>case_claims (CaseId) WHERE ReleasedAt IS
/// NULL</c> enforces at-most-one-active-claim per case.
/// </para>
/// </summary>
public sealed class AnalysisService : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name. The "All Locations" service has Name read-only at the admin layer.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-text description for ops. Optional.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// True for the immutable "All Locations" service auto-created per
    /// tenant. The unique partial index on
    /// <c>(TenantId) WHERE IsBuiltInAllLocations</c> enforces at most
    /// one per tenant; deletion is rejected by both the service-layer
    /// guard and a database trigger.
    /// </summary>
    public bool IsBuiltInAllLocations { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public long TenantId { get; set; }
}
