using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// N:N junction between <see cref="AnalysisService"/> and
/// <see cref="Location"/> (VP6, locked 2026-05-02). A location can belong
/// to multiple services; a service can cover multiple locations.
/// Composite primary key <c>(AnalysisServiceId, LocationId)</c>.
///
/// <para>
/// The "All Locations" service auto-claims every location at tenant /
/// location creation time via the bootstrap migration + the location
/// auto-join hook (Phase A.5). Removing a location from the "All
/// Locations" service is rejected by service-layer guard.
/// </para>
///
/// <para>
/// <see cref="TenantId"/> is denormalised here so the
/// <c>tenant_isolation_*</c> RLS policy can filter without joining
/// through <see cref="AnalysisService"/>.
/// </para>
/// </summary>
public sealed class AnalysisServiceLocation : ITenantOwned
{
    public Guid AnalysisServiceId { get; set; }
    public AnalysisService? AnalysisService { get; set; }

    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    public long TenantId { get; set; }
}
