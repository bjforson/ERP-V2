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

    /// <summary>
    /// Sprint 45 / Phase D — per-tier breakdown for the dashboard's
    /// "by tier" cards. Returns one row per
    /// <see cref="QueueTier"/>, ordered by escalation priority
    /// (Urgent → High → Standard → Exception → PostClearance). Used
    /// by the SlaDashboard razor page to render coloured cards
    /// reflecting current queue load + breach pressure per tier.
    /// </summary>
    public async Task<IReadOnlyList<SlaTierBreakdown>> ListByTierAsync(
        TimeSpan? closedLookback = null,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException("SlaDashboardService cannot run without a resolved tenant context.");
        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;
        var atRiskFraction = _options.Value.AtRiskFraction;
        var sinceClosed = now - (closedLookback ?? TimeSpan.FromDays(7));

        var openRows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.ClosedAt == null)
            .ToListAsync(ct);
        var closedRows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.TenantId == tenantId
                     && w.ClosedAt != null
                     && w.ClosedAt >= sinceClosed)
            .ToListAsync(ct);

        // Recompute open state in-memory so the cards reflect "now".
        foreach (var w in openRows)
            w.State = SlaTracker.ComputeOpenState(w, now, atRiskFraction);

        // One row per tier, ordered by escalation priority.
        var orderedTiers = new[]
        {
            QueueTier.Urgent, QueueTier.High, QueueTier.Standard,
            QueueTier.Exception, QueueTier.PostClearance
        };

        var rows = new List<SlaTierBreakdown>(orderedTiers.Length);
        foreach (var tier in orderedTiers)
        {
            var openInTier = openRows.Where(w => w.QueueTier == tier).ToList();
            var closedInTier = closedRows.Where(w => w.QueueTier == tier).ToList();
            rows.Add(new SlaTierBreakdown(
                Tier: tier,
                OpenOnTime: openInTier.Count(w => w.State == SlaWindowState.OnTime),
                OpenAtRisk: openInTier.Count(w => w.State == SlaWindowState.AtRisk),
                OpenBreached: openInTier.Count(w => w.State == SlaWindowState.Breached),
                ClosedOnTime: closedInTier.Count(w => w.State == SlaWindowState.Closed),
                ClosedBreached: closedInTier.Count(w => w.State == SlaWindowState.Breached),
                ManualCount: openInTier.Count(w => w.QueueTierIsManual)));
        }
        return rows;
    }

    /// <summary>
    /// Sprint 49 / FU-sla-trend-sparkline — per-day trend buckets for
    /// the dashboard's "Trend" card sparkline. Returns one row per UTC
    /// day inside the trailing window, ordered chronologically (oldest
    /// first) so a sparkline path renders left-to-right naturally.
    ///
    /// <para>Each row carries:
    /// <list type="bullet">
    ///   <item><c>Opened</c> — windows whose <c>StartedAt</c> falls in the bucket.</item>
    ///   <item><c>Closed</c> — windows whose <c>ClosedAt</c> falls in the bucket.</item>
    ///   <item><c>Breached</c> — windows that breached at any point in the bucket
    ///         (closed-with-State=Breached anchored on ClosedAt, OR open-with-DueAt
    ///         in the bucket and the window already past due as of "now").</item>
    /// </list>
    /// </para>
    ///
    /// <para>Defaults to a 14-day window. The query pulls every window
    /// in the [now - days .. now] range and groups in-memory; an
    /// in-memory grouping is fine because the dashboard only renders the
    /// last few weeks and an admin tenant has tens-to-hundreds of rows
    /// per day at most. If that ever stops being true the read can move
    /// to a SQL DATE_TRUNC.</para>
    /// </summary>
    public async Task<IReadOnlyList<SlaTrendBucket>> GetTrendAsync(
        int days = 14,
        CancellationToken ct = default)
    {
        if (days <= 0) days = 14;
        if (!_tenant.IsResolved)
            throw new InvalidOperationException("SlaDashboardService cannot run without a resolved tenant context.");

        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;
        // Anchor every bucket on UTC midnight of its day so timezone
        // drift can't move a row between days mid-render.
        var todayUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var firstBucket = todayUtc.AddDays(-(days - 1));
        var rangeStart = firstBucket;
        var rangeEnd = todayUtc.AddDays(1); // exclusive upper bound

        // Pull every window touched by the range. A window may not have
        // started in-range but still closed in-range, so we OR the
        // started/closed clauses; this is a small read for typical
        // dashboards (low hundreds of rows per day).
        var rows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.TenantId == tenantId
                     && (w.StartedAt >= rangeStart
                      || (w.ClosedAt != null && w.ClosedAt >= rangeStart)
                      || w.DueAt >= rangeStart))
            .Where(w => w.StartedAt < rangeEnd
                     || (w.ClosedAt != null && w.ClosedAt < rangeEnd)
                     || w.DueAt < rangeEnd)
            .ToListAsync(ct);

        var buckets = new List<SlaTrendBucket>(days);
        for (var i = 0; i < days; i++)
        {
            var bucketStart = firstBucket.AddDays(i);
            var bucketEnd = bucketStart.AddDays(1);

            var opened = rows.Count(w => w.StartedAt >= bucketStart && w.StartedAt < bucketEnd);
            var closed = rows.Count(w => w.ClosedAt is not null
                                      && w.ClosedAt >= bucketStart
                                      && w.ClosedAt < bucketEnd);
            // Breached count: closed-and-breached anchored on ClosedAt
            // (the natural close moment), plus open-and-past-due
            // anchored on DueAt (the breach moment for an open row).
            var breachedClosed = rows.Count(w => w.ClosedAt is not null
                                              && w.State == SlaWindowState.Breached
                                              && w.ClosedAt >= bucketStart
                                              && w.ClosedAt < bucketEnd);
            var breachedOpen = rows.Count(w => w.ClosedAt is null
                                            && w.DueAt < now
                                            && w.DueAt >= bucketStart
                                            && w.DueAt < bucketEnd);

            buckets.Add(new SlaTrendBucket(
                Day: bucketStart,
                Opened: opened,
                Closed: closed,
                Breached: breachedClosed + breachedOpen));
        }
        return buckets;
    }

    /// <summary>
    /// Sprint 45 / Phase D — operator-driven manual reclassify. Sets
    /// the SlaWindow's tier + flips the manual flag so the
    /// QueueEscalatorWorker leaves it alone. Returns true on success,
    /// false when no row matched.
    /// </summary>
    public async Task<bool> ReclassifyTierAsync(
        Guid slaWindowId,
        QueueTier tier,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException("SlaDashboardService cannot run without a resolved tenant context.");
        var tenantId = _tenant.TenantId;
        var row = await _db.SlaWindows
            .FirstOrDefaultAsync(w => w.Id == slaWindowId && w.TenantId == tenantId, ct);
        if (row is null) return false;
        row.QueueTier = tier;
        row.QueueTierIsManual = true;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "SlaDashboardService: reclassified window {Id} to tier {Tier} (manual=true) under tenant {Tenant}.",
            slaWindowId, tier, tenantId);
        return true;
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

/// <summary>
/// Sprint 45 / Phase D — per-tier breakdown row for the SLA dashboard's
/// "by tier" cards. <see cref="ManualCount"/> is the count of open
/// windows in this tier that an operator manually assigned (the
/// QueueEscalatorWorker won't auto-escalate them).
/// </summary>
public sealed record SlaTierBreakdown(
    QueueTier Tier,
    int OpenOnTime,
    int OpenAtRisk,
    int OpenBreached,
    int ClosedOnTime,
    int ClosedBreached,
    int ManualCount);

/// <summary>
/// Sprint 49 / FU-sla-trend-sparkline — one day's worth of activity in
/// the trend window. <see cref="Day"/> is the UTC midnight of the
/// bucket; counts are inclusive of all SLA windows that opened, closed
/// or breached inside the bucket. Buckets are returned chronologically.
/// </summary>
public sealed record SlaTrendBucket(
    DateTimeOffset Day,
    int Opened,
    int Closed,
    int Breached);
