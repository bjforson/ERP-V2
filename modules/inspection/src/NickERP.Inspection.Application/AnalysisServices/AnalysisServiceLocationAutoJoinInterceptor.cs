using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;

namespace NickERP.Inspection.Application.AnalysisServices;

/// <summary>
/// VP6 (locked 2026-05-02) Phase A.5 — SaveChanges interceptor that
/// automatically adds an <c>AnalysisServiceLocation</c> junction row
/// for every <c>Location</c> being inserted, joining it to the
/// tenant's "All Locations" <c>AnalysisService</c>.
///
/// <para>
/// **Why an interceptor.** Locations are created via several paths
/// today (admin Razor page, future scanner-onboarding wizards,
/// migration backfills, possibly programmatic test fixtures). A
/// service-layer wrapper around the create call would have to be
/// adopted at every site. An EF SaveChanges interceptor catches the
/// insert no matter where it originates — single point of enforcement.
/// </para>
///
/// <para>
/// **Same transaction as the location insert.** The interceptor adds
/// the junction entity to the same <c>ChangeTracker</c> before
/// SaveChanges runs the INSERT batch. Both rows commit atomically; an
/// auto-join can never be left missing relative to its location.
/// </para>
///
/// <para>
/// **Missing All Locations service.** If the tenant somehow has no
/// <c>IsBuiltInAllLocations = TRUE</c> service yet (not the bootstrap
/// flow's intended state, but conceivable for tenants seeded by direct
/// SQL), the interceptor logs a WARNING and proceeds without adding
/// the junction. The location will not be auto-joined. Operator can
/// fix-forward by calling
/// <see cref="IAnalysisServiceBootstrap.EnsureAllLocationsServiceAsync"/>
/// for the tenant + manually adding the junction.
/// </para>
///
/// <para>
/// **Race with parent insert.** When a brand-new tenant's first
/// location is inserted in the same SaveChanges call as the bootstrap
/// service row, the interceptor sees the in-flight service entity in
/// the change tracker and uses its Id. (The portal flow calls
/// EnsureAllLocationsServiceAsync — which SaveChanges separately —
/// BEFORE the location insert, so this is only the safety-net path.)
/// </para>
/// </summary>
public sealed class AnalysisServiceLocationAutoJoinInterceptor : SaveChangesInterceptor
{
    private readonly ILogger<AnalysisServiceLocationAutoJoinInterceptor> _logger;

    public AnalysisServiceLocationAutoJoinInterceptor(
        ILogger<AnalysisServiceLocationAutoJoinInterceptor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is InspectionDbContext db)
        {
            QueueAutoJoinRows(db);
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is InspectionDbContext db)
        {
            QueueAutoJoinRows(db);
        }
        return base.SavingChanges(eventData, result);
    }

    private void QueueAutoJoinRows(InspectionDbContext db)
    {
        // Snapshot the locations that are being newly inserted in this
        // SaveChanges. We don't enumerate the change tracker live
        // because adding a new entity to the tracker mid-iteration
        // would risk invalidating the iterator.
        var newLocations = db.ChangeTracker
            .Entries<Location>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToArray();

        if (newLocations.Length == 0)
        {
            return;
        }

        // Group by tenant — a single SaveChanges might span tenants
        // under SetSystemContext (e.g., a cross-tenant admin flow).
        // Most calls have a single tenant; this generalises cleanly.
        foreach (var group in newLocations.GroupBy(l => l.TenantId))
        {
            var tenantId = group.Key;

            // Find the All Locations service id. First check the
            // change tracker (covers brand-new-tenant flows where the
            // service was just added). Then check the DB.
            var serviceId = FindAllLocationsServiceId(db, tenantId);

            if (serviceId == Guid.Empty)
            {
                _logger.LogWarning(
                    "Tenant {TenantId} has no All Locations AnalysisService; "
                    + "skipping auto-join for {LocationCount} new location(s). "
                    + "Operator should call EnsureAllLocationsServiceAsync to fix forward.",
                    tenantId, group.Count());
                continue;
            }

            foreach (var location in group)
            {
                // Skip if a junction already exists in the change
                // tracker (defensive — caller might have queued one
                // explicitly).
                var alreadyQueued = db.ChangeTracker
                    .Entries<AnalysisServiceLocation>()
                    .Any(e =>
                        e.State == EntityState.Added
                        && e.Entity.AnalysisServiceId == serviceId
                        && e.Entity.LocationId == location.Id);

                if (alreadyQueued)
                {
                    continue;
                }

                db.AnalysisServiceLocations.Add(new AnalysisServiceLocation
                {
                    AnalysisServiceId = serviceId,
                    LocationId = location.Id,
                    TenantId = tenantId,
                    AddedAt = DateTimeOffset.UtcNow,
                });
            }
        }
    }

    private static Guid FindAllLocationsServiceId(InspectionDbContext db, long tenantId)
    {
        // Check the change tracker first — covers the same-SaveChanges
        // bootstrap-then-location case.
        var trackedEntry = db.ChangeTracker
            .Entries<AnalysisService>()
            .FirstOrDefault(e =>
                e.Entity.TenantId == tenantId
                && e.Entity.IsBuiltInAllLocations
                && (e.State == EntityState.Added || e.State == EntityState.Unchanged));

        if (trackedEntry is not null)
        {
            return trackedEntry.Entity.Id;
        }

        // Fall back to the DB. RLS narrows by app.tenant_id; if the
        // calling scope has the right tenant set, this returns the row
        // (or empty for tenants without a service yet).
        return db.AnalysisServices
            .Where(s => s.TenantId == tenantId && s.IsBuiltInAllLocations)
            .Select(s => s.Id)
            .FirstOrDefault();
    }
}
