using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 31 / B5.1 — SLA dashboard read service. Backs the
/// <c>/admin/sla</c> Razor page.
///
/// <para>
/// Read-only: pulls open + closed <see cref="SlaWindow"/> rows for the
/// current tenant and computes summary cards (open count, breach
/// count, on-time count) + a per-window-name breakdown. Mirrors the
/// v1 NSCIM "ICUMS dashboard" panels but vendor-neutralised — no Ghana
/// strings live here. Adapter modules can render their own dashboards
/// (e.g. CustomsGh-specific port-throughput dashboards) without
/// touching this code.
/// </para>
/// </summary>
public sealed class SlaDashboardService
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IOptions<SlaTrackerOptions> _options;
    private readonly ILogger<SlaDashboardService> _logger;

    public SlaDashboardService(
        InspectionDbContext db,
        ITenantContext tenant,
        IOptions<SlaTrackerOptions> options,
        ILogger<SlaDashboardService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Build the dashboard summary. Refreshes the live state of every
    /// open window first (so AtRisk → Breached transitions show up
    /// without waiting for the next state-change event), then
    /// aggregates by state + by window name.
    /// </summary>
    public async Task<SlaDashboardSummary> BuildSummaryAsync(
        TimeSpan? closedLookback = null,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException("SlaDashboardService cannot run without a resolved tenant context.");

        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;
        var atRiskFraction = _options.Value.AtRiskFraction;
        var sinceClosed = now - (closedLookback ?? TimeSpan.FromDays(7));

        // Pull every open window for the tenant + every closed window
        // in the lookback window. State recomputation happens
        // in-memory so the dashboard reflects "now" without a write.
        var openRows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.ClosedAt == null)
            .ToListAsync(ct);
        var closedRows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.TenantId == tenantId
                     && w.ClosedAt != null
                     && w.ClosedAt >= sinceClosed)
            .ToListAsync(ct);

        // Recompute open state in-memory.
        foreach (var w in openRows)
            w.State = SlaTracker.ComputeOpenState(w, now, atRiskFraction);

        var openByState = openRows
            .GroupBy(w => w.State)
            .ToDictionary(g => g.Key, g => g.Count());
        var closedByState = closedRows
            .GroupBy(w => w.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var byWindow = openRows.Concat(closedRows)
            .GroupBy(w => w.WindowName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SlaWindowBreakdown(
                WindowName: g.Key,
                OpenOnTime: g.Count(w => w.ClosedAt == null && w.State == SlaWindowState.OnTime),
                OpenAtRisk: g.Count(w => w.ClosedAt == null && w.State == SlaWindowState.AtRisk),
                OpenBreached: g.Count(w => w.ClosedAt == null && w.State == SlaWindowState.Breached),
                ClosedOnTime: g.Count(w => w.ClosedAt != null && w.State == SlaWindowState.Closed),
                ClosedBreached: g.Count(w => w.ClosedAt != null && w.State == SlaWindowState.Breached),
                AverageDurationMinutes: AverageDurationMinutes(g.Where(w => w.ClosedAt != null).ToList())))
            .OrderBy(b => b.WindowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SlaDashboardSummary(
            GeneratedAt: now,
            OpenOnTime: openByState.GetValueOrDefault(SlaWindowState.OnTime),
            OpenAtRisk: openByState.GetValueOrDefault(SlaWindowState.AtRisk),
            OpenBreached: openByState.GetValueOrDefault(SlaWindowState.Breached),
            ClosedOnTime: closedByState.GetValueOrDefault(SlaWindowState.Closed),
            ClosedBreached: closedByState.GetValueOrDefault(SlaWindowState.Breached),
            ByWindow: byWindow);
    }

    /// <summary>
    /// List the top-N breached windows for the dashboard's "drill in"
    /// table. Ordered by oldest breach first so the most painful rows
    /// land at the top.
    /// </summary>
    public async Task<IReadOnlyList<SlaBreachedRow>> ListBreachedAsync(
        int take = 50,
        CancellationToken ct = default)
    {
        if (take <= 0) take = 50;
        if (!_tenant.IsResolved)
            throw new InvalidOperationException("SlaDashboardService cannot run without a resolved tenant context.");
        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;

        // Open windows past their deadline OR closed windows that
        // missed their deadline. The covering index
        // ix_sla_window_tenant_state_due is a perfect fit for the
        // open-and-due query.
        var pastOpen = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.TenantId == tenantId
                     && w.ClosedAt == null
                     && w.DueAt < now)
            .OrderBy(w => w.DueAt)
            .Take(take)
            .ToListAsync(ct);
        var closedBreached = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.TenantId == tenantId
                     && w.ClosedAt != null
                     && w.State == SlaWindowState.Breached)
            .OrderByDescending(w => w.ClosedAt)
            .Take(take)
            .ToListAsync(ct);

        return pastOpen.Concat(closedBreached)
            .Select(w => new SlaBreachedRow(
                Id: w.Id,
                CaseId: w.CaseId,
                WindowName: w.WindowName,
                StartedAt: w.StartedAt,
                DueAt: w.DueAt,
                ClosedAt: w.ClosedAt,
                BudgetMinutes: w.BudgetMinutes,
                IsOpen: w.ClosedAt is null,
                MinutesOver: ComputeMinutesOver(w, now)))
            .OrderByDescending(r => r.MinutesOver)
            .Take(take)
            .ToList();
    }

    private static double ComputeMinutesOver(SlaWindow w, DateTimeOffset now)
    {
        var anchor = w.ClosedAt ?? now;
        return (anchor - w.DueAt).TotalMinutes;
    }

    private static double? AverageDurationMinutes(IReadOnlyList<SlaWindow> closed)
    {
        if (closed.Count == 0) return null;
        return closed.Average(w => (w.ClosedAt!.Value - w.StartedAt).TotalMinutes);
    }
}

/// <summary>Top-of-page summary cards for the SLA dashboard.</summary>
public sealed record SlaDashboardSummary(
    DateTimeOffset GeneratedAt,
    int OpenOnTime,
    int OpenAtRisk,
    int OpenBreached,
    int ClosedOnTime,
    int ClosedBreached,
    IReadOnlyList<SlaWindowBreakdown> ByWindow);

/// <summary>One row of the per-window breakdown table.</summary>
public sealed record SlaWindowBreakdown(
    string WindowName,
    int OpenOnTime,
    int OpenAtRisk,
    int OpenBreached,
    int ClosedOnTime,
    int ClosedBreached,
    double? AverageDurationMinutes);

/// <summary>Drill-in row for the breached-window table.</summary>
public sealed record SlaBreachedRow(
    Guid Id,
    Guid CaseId,
    string WindowName,
    DateTimeOffset StartedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ClosedAt,
    int BudgetMinutes,
    bool IsOpen,
    double MinutesOver);
