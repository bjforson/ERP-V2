using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Application.AnalysisServices;

/// <summary>
/// Sprint 14 / VP6 Phase C — case-visibility helper. Encapsulates the
/// per-tenant Shared / Exclusive routing rules so admin pages, the
/// analyst Cases list, and tests share one implementation.
///
/// <para>
/// **Shared mode (default).** A user sees every case whose location is
/// covered by any of the user's <see cref="AnalysisService"/>
/// memberships. Joins: <c>AnalysisServiceUser</c> →
/// <c>AnalysisServiceLocation</c> → <c>Location</c>; the
/// case-visibility predicate is <c>Cases.LocationId IN (those locations)</c>.
/// First-claim-wins prevents two analysts from working the same case
/// (enforced by <see cref="CaseClaimService"/>).
/// </para>
///
/// <para>
/// **Exclusive mode.** Same set of candidate cases as shared, BUT each
/// case routes to exactly one winning service per the
/// most-specific-service-wins rule: smallest scope wins (FEWEST owned
/// locations), ties broken by oldest <see cref="AnalysisService.CreatedAt"/>.
/// A user sees a case in exclusive mode only when their service IS the
/// winning service. Implemented in two passes: (1) pull candidate cases
/// + their location ids; (2) for each location, compute the winning
/// service id via an in-memory ranking of qualifying services. The
/// ranking is cached per-tenant request scope (the helper instance
/// itself is request-scoped).
/// </para>
///
/// <para>
/// **All Locations.** The built-in service participates in the ranking
/// like any other service — but its scope is universal so it always has
/// the LARGEST location count for its tenant. Under exclusive mode, a
/// dedicated per-location service ALWAYS beats All Locations because
/// its scope is smaller; this is by design (specialised teams pre-empt
/// the universal fallback). Under shared mode, All Locations always
/// matches as long as the user is a member.
/// </para>
/// </summary>
public sealed class CaseVisibilityService
{
    private readonly InspectionDbContext _db;
    private readonly TenancyDbContext _tenancyDb;
    private readonly ITenantContext _tenant;
    private readonly ILogger<CaseVisibilityService> _logger;

    public CaseVisibilityService(
        InspectionDbContext db,
        TenancyDbContext tenancyDb,
        ITenantContext tenant,
        ILogger<CaseVisibilityService> logger)
    {
        _db = db;
        _tenancyDb = tenancyDb;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>
    /// Return the set of <see cref="Location"/> ids a user can reach
    /// through <see cref="AnalysisServiceUser"/> membership joined via
    /// <see cref="AnalysisServiceLocation"/>. Multi-membership is
    /// handled by union; the set is distinct.
    /// </summary>
    public async Task<List<Guid>> GetAccessibleLocationIdsAsync(Guid userId, CancellationToken ct = default)
    {
        EnsureTenantResolved();
        return await _db.AnalysisServiceUsers.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Join(_db.AnalysisServiceLocations.AsNoTracking(),
                  u => u.AnalysisServiceId, asl => asl.AnalysisServiceId,
                  (u, asl) => asl.LocationId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Return the set of cases a user can see, applying the tenant's
    /// <see cref="Tenant.CaseVisibilityModel"/>. Caller can narrow
    /// further (location filter, state filter, paging) on top of the
    /// returned IQueryable, but only for shared mode — exclusive mode
    /// must filter the materialised list because the winning-service
    /// computation is per-case.
    /// </summary>
    public async Task<List<InspectionCase>> GetVisibleCasesAsync(
        Guid userId,
        int take = 100,
        Guid? locationFilter = null,
        InspectionWorkflowState? stateFilter = null,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();

        var tenantRow = await _tenancyDb.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == _tenant.TenantId, ct)
            .ConfigureAwait(false);
        var visibility = tenantRow?.CaseVisibilityModel ?? CaseVisibilityModel.Shared;

        var accessibleLocations = await GetAccessibleLocationIdsAsync(userId, ct).ConfigureAwait(false);
        if (accessibleLocations.Count == 0) return new List<InspectionCase>();

        var q = _db.Cases.AsNoTracking()
            .Where(c => accessibleLocations.Contains(c.LocationId));
        if (locationFilter is { } loc) q = q.Where(c => c.LocationId == loc);
        if (stateFilter is { } st) q = q.Where(c => c.State == st);

        var candidates = await q.OrderByDescending(c => c.OpenedAt)
            .Take(take * (visibility == CaseVisibilityModel.Exclusive ? 4 : 1))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (visibility == CaseVisibilityModel.Shared)
            return candidates.Take(take).ToList();

        // Exclusive routing — for each candidate case, compute the
        // winning service for its location and keep the case only when
        // the user is a member of the winner.
        var userServices = await _db.AnalysisServiceUsers.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => u.AnalysisServiceId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (userServices.Count == 0) return new List<InspectionCase>();
        var userServiceSet = userServices.ToHashSet();

        var locationsInPlay = candidates.Select(c => c.LocationId).Distinct().ToList();
        var winnerByLocation = await ComputeExclusiveWinnersAsync(locationsInPlay, ct).ConfigureAwait(false);

        return candidates
            .Where(c => winnerByLocation.TryGetValue(c.LocationId, out var winner)
                        && userServiceSet.Contains(winner))
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// Compute the most-specific-service-wins routing per location for
    /// the given location set. Smallest scope (fewest locations) wins;
    /// ties broken by oldest <see cref="AnalysisService.CreatedAt"/>.
    /// Returns a dictionary <c>LocationId → winning AnalysisServiceId</c>;
    /// locations with no qualifying service are absent from the result.
    /// </summary>
    public async Task<Dictionary<Guid, Guid>> ComputeExclusiveWinnersAsync(
        IReadOnlyCollection<Guid> locationIds,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        if (locationIds.Count == 0) return new Dictionary<Guid, Guid>();

        // Pull all (Location → Service) edges for the relevant locations,
        // joined to AnalysisService for the CreatedAt tiebreaker.
        var edges = await _db.AnalysisServiceLocations.AsNoTracking()
            .Where(asl => locationIds.Contains(asl.LocationId))
            .Join(_db.AnalysisServices.AsNoTracking(),
                  asl => asl.AnalysisServiceId, s => s.Id,
                  (asl, s) => new { asl.LocationId, ServiceId = s.Id, s.CreatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Per-service location count — smaller = more specific.
        var locCountByService = await _db.AnalysisServiceLocations.AsNoTracking()
            .GroupBy(asl => asl.AnalysisServiceId)
            .Select(g => new { ServiceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ServiceId, x => x.Count, ct)
            .ConfigureAwait(false);

        var winners = new Dictionary<Guid, Guid>();
        foreach (var grp in edges.GroupBy(e => e.LocationId))
        {
            var winner = grp
                .Select(e => new
                {
                    e.ServiceId,
                    LocCount = locCountByService.TryGetValue(e.ServiceId, out var lc) ? lc : int.MaxValue,
                    e.CreatedAt
                })
                .OrderBy(x => x.LocCount)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.ServiceId) // final deterministic tiebreaker
                .FirstOrDefault();
            if (winner is not null) winners[grp.Key] = winner.ServiceId;
        }
        return winners;
    }

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; CaseVisibilityService must run inside a tenant-aware request scope.");
    }
}
