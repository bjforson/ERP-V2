namespace NickERP.Inspection.Application.AnalysisServices;

/// <summary>
/// VP6 (locked 2026-05-02) Phase A.5 — auto-bootstrap hook for the
/// "All Locations" <c>AnalysisService</c> per tenant.
///
/// <para>
/// **Why a hook is needed.** The Phase A
/// <c>BootstrapAnalysisServicesV0</c> migration stamps the "All
/// Locations" service for every tenant that already had inspection-side
/// data when the migration ran. A tenant created AFTER the migration
/// (e.g. via the portal's <c>Tenants.razor</c>) would have no service
/// row, and any later location insert would skip auto-join. This
/// interface gives portal-side / cross-DB callers a way to ensure the
/// service exists for a tenant on demand.
/// </para>
///
/// <para>
/// **Idempotent.** Calling <see cref="EnsureAllLocationsServiceAsync"/>
/// multiple times is safe; the unique partial index on
/// <c>analysis_services (TenantId) WHERE IsBuiltInAllLocations</c>
/// guarantees at most one row per tenant. The implementation uses
/// <c>WHERE NOT EXISTS</c> + raw INSERT to avoid race-condition
/// double-creates.
/// </para>
///
/// <para>
/// Locations don't have a separate ensure-method; the
/// <see cref="AnalysisServiceLocationAutoJoinInterceptor"/> in
/// <c>NickERP.Inspection.Database</c> intercepts every <c>Location</c>
/// insert through <c>InspectionDbContext</c> and queues the matching
/// <c>AnalysisServiceLocation</c> row in the same SaveChanges
/// transaction.
/// </para>
/// </summary>
public interface IAnalysisServiceBootstrap
{
    /// <summary>
    /// Idempotently ensure the "All Locations" <c>AnalysisService</c>
    /// row exists for the given tenant. Returns true when a row was
    /// created in this call; false when one already existed.
    /// </summary>
    /// <param name="tenantId">Tenant whose "All Locations" service should exist.</param>
    /// <param name="createdByUserId">Optional admin user id that triggered the bootstrap. Stored on the row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> EnsureAllLocationsServiceAsync(
        long tenantId,
        Guid? createdByUserId,
        CancellationToken cancellationToken = default);
}
