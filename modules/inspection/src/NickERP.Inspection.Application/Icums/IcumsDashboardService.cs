using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Icums;

/// <summary>
/// Sprint 22 / B2.3 — read-only dashboard service for the
/// <c>/admin/icums</c> landing page. Composes summary cards from the
/// per-area admin services (submission status counts + download type
/// counts + active external systems + recent ICUMS-flavoured audit
/// events).
///
/// <para>
/// Currently shells out to the underlying entities directly to keep the
/// dashboard a thin aggregator; if those queries grow expensive enough
/// to warrant caching, the obvious split is to push each card into its
/// own service + a 5-second per-tenant cache (mirroring the §6.5
/// threshold-resolver pattern).
/// </para>
/// </summary>
public sealed class IcumsDashboardService
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<IcumsDashboardService> _logger;

    public IcumsDashboardService(
        InspectionDbContext db,
        ITenantContext tenant,
        ILogger<IcumsDashboardService> logger)
    {
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>
    /// Render the dashboard summary in a single round-trip-ish call.
    /// </summary>
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        EnsureTenantResolved();

        var submissionCounts = await _db.OutboundSubmissions.AsNoTracking()
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var downloadCounts = await _db.AuthorityDocuments.AsNoTracking()
            .GroupBy(d => d.DocumentType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var externalSystems = await _db.ExternalSystemInstances.AsNoTracking()
            .Where(e => e.IsActive)
            .OrderBy(e => e.DisplayName)
            .Select(e => new ExternalSystemSummary(
                e.Id,
                e.TypeCode,
                e.DisplayName,
                e.Scope.ToString(),
                e.Bindings.Count))
            .ToListAsync(ct).ConfigureAwait(false);

        var unmatchedDocCount = await _db.AuthorityDocuments.AsNoTracking()
            .CountAsync(d => d.CaseId == Guid.Empty, ct).ConfigureAwait(false);

        return new DashboardSummary(
            SubmissionCountsByStatus: submissionCounts.ToDictionary(x => x.Status, x => x.Count, StringComparer.OrdinalIgnoreCase),
            DownloadCountsByType: downloadCounts.ToDictionary(x => x.Type, x => x.Count, StringComparer.OrdinalIgnoreCase),
            ExternalSystems: externalSystems,
            UnmatchedDocumentCount: unmatchedDocCount);
    }

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; IcumsDashboardService must run inside a tenant-aware request scope.");
    }
}

/// <summary>Aggregated view for the ICUMS dashboard.</summary>
public sealed record DashboardSummary(
    IReadOnlyDictionary<string, int> SubmissionCountsByStatus,
    IReadOnlyDictionary<string, int> DownloadCountsByType,
    IReadOnlyList<ExternalSystemSummary> ExternalSystems,
    int UnmatchedDocumentCount);

/// <summary>One row in the active-external-systems card.</summary>
public sealed record ExternalSystemSummary(
    Guid Id,
    string TypeCode,
    string DisplayName,
    string Scope,
    int BindingCount);
