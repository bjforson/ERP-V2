using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Assigns a canonical <c>NickERP.Platform.Identity.Entities.IdentityUser</c>
/// to a <see cref="Location"/> with per-location role(s). The piece that
/// makes federation real — without this, a user belongs to the tenant
/// globally; with it, a user only sees the locations they're assigned to.
/// </summary>
/// <remarks>
/// Lives in inspection (not identity) because Location is an inspection
/// concept. The inspection module's middleware / services read these to
/// scope queries by <c>LocationId IN (allowed)</c>.
///
/// Soft revoke via <see cref="IsActive"/>; never hard-delete because
/// audit references must remain joinable.
/// </remarks>
public sealed class LocationAssignment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Canonical user id from <c>Platform.Identity.Entities.IdentityUser.Id</c>. No FK across modules; the application enforces referential integrity.</summary>
    public Guid IdentityUserId { get; set; }

    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>Comma-separated location-scoped role codes — "analyst", "supervisor", "operator", "location-admin". Module-specific; not enforced as enum.</summary>
    public string Roles { get; set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>The admin who granted the assignment.</summary>
    public Guid GrantedByUserId { get; set; }

    /// <summary>Optional expiry — assignment automatically inactive past this date.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Soft revoke. Resolver excludes inactive rows.</summary>
    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }

    public long TenantId { get; set; }
}
