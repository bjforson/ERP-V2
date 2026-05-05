using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Sla;

/// <summary>
/// Sprint 31 / B5.1 — vendor-neutral SLA window tracker.
///
/// <para>
/// Persists <see cref="SlaWindow"/> rows in <c>inspection.sla_window</c>.
/// Per-tenant budget overrides come from
/// <c>tenancy.tenant_sla_settings</c> via
/// <see cref="ISlaSettingsProvider"/>; missing rows fall back to the
/// engine default budgets in <see cref="SlaTrackerOptions"/>.
/// </para>
///
/// <para>
/// <b>State recomputation</b>. <see cref="RefreshStatesAsync"/> reclassifies
/// still-open windows: rows past <see cref="SlaWindow.DueAt"/> become
/// <c>Breached</c>; rows ≥<see cref="SlaTrackerOptions.AtRiskFraction"/>
/// of budget elapsed become <c>AtRisk</c>; otherwise <c>OnTime</c>.
/// Closed rows are left alone (their state was set on close and the
/// audit trail says "what happened then, not what would happen now").
/// </para>
/// </summary>
public sealed class SlaTracker : ISlaTracker
{
    /// <summary>Standard window name — case open → first Validated transition.</summary>
    public const string OpenToValidated = "case.open_to_validated";
    /// <summary>Standard window name — Validated → first Verdict transition.</summary>
    public const string ValidatedToVerdict = "case.validated_to_verdict";
    /// <summary>Standard window name — Verdict → Submitted transition.</summary>
    public const string VerdictToSubmitted = "case.verdict_to_submitted";

    /// <summary>The standard windows opened on case creation.</summary>
    public static readonly IReadOnlyList<string> StandardWindows = new[]
    {
        OpenToValidated, ValidatedToVerdict, VerdictToSubmitted
    };

    private readonly InspectionDbContext _db;
    private readonly ISlaSettingsProvider _settings;
    private readonly ITenantContext _tenant;
    private readonly IOptions<SlaTrackerOptions> _options;
    private readonly ILogger<SlaTracker> _logger;

    public SlaTracker(
        InspectionDbContext db,
        ISlaSettingsProvider settings,
        ITenantContext tenant,
        IOptions<SlaTrackerOptions> options,
        ILogger<SlaTracker> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<SlaWindow>> OpenWindowsAsync(
        Guid caseId,
        IReadOnlyCollection<string> windowNames,
        DateTimeOffset openedAt,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "SlaTracker cannot open windows without a resolved tenant context.");
        var tenantId = _tenant.TenantId;
        if (windowNames.Count == 0) return Array.Empty<SlaWindow>();

        var existing = await _db.Set<SlaWindow>()
            .Where(w => w.CaseId == caseId && windowNames.Contains(w.WindowName) && w.ClosedAt == null)
            .ToListAsync(ct);
        var existingByName = existing.ToDictionary(
            w => w.WindowName,
            StringComparer.OrdinalIgnoreCase);

        var settings = await _settings.GetSettingsAsync(tenantId, ct);
        var defaults = _options.Value.DefaultBudgets;
        var opened = new List<SlaWindow>(windowNames.Count);
        foreach (var name in windowNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existingByName.TryGetValue(name, out var prior))
            {
                opened.Add(prior);
                continue;
            }

            // Per-tenant override → engine default → fall back to 24h
            // (visible default so missing config + no per-tenant row
            // never silently no-ops).
            int budget;
            if (settings.TryGetValue(name, out var snap))
            {
                if (!snap.Enabled) continue; // tenant disabled this window
                budget = snap.TargetMinutes;
            }
            else if (defaults.TryGetValue(name, out var def))
            {
                budget = def;
            }
            else
            {
                budget = _options.Value.FallbackBudgetMinutes;
            }
            if (budget <= 0) continue; // 0/negative budget → window disabled

            var window = new SlaWindow
            {
                Id = Guid.NewGuid(),
                CaseId = caseId,
                WindowName = name,
                StartedAt = openedAt,
                DueAt = openedAt.AddMinutes(budget),
                State = SlaWindowState.OnTime,
                BudgetMinutes = budget,
                TenantId = tenantId
            };
            _db.Set<SlaWindow>().Add(window);
            opened.Add(window);
        }

        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(ct);
        return opened;
    }

    public Task<IReadOnlyList<SlaWindow>> OpenStandardWindowsAsync(
        Guid caseId,
        DateTimeOffset openedAt,
        CancellationToken ct = default)
        => OpenWindowsAsync(caseId, StandardWindows, openedAt, ct);

    public async Task<int> CloseAllOpenWindowsAsync(
        Guid caseId,
        DateTimeOffset closedAt,
        CancellationToken ct = default)
    {
        var rows = await _db.Set<SlaWindow>()
            .Where(w => w.CaseId == caseId && w.ClosedAt == null)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;
        foreach (var w in rows) ApplyClose(w, closedAt);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<bool> CloseWindowAsync(
        Guid caseId,
        string windowName,
        DateTimeOffset closedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(windowName)) return false;
        var nameLower = windowName.ToLowerInvariant();
        var row = await _db.Set<SlaWindow>()
            .FirstOrDefaultAsync(
                w => w.CaseId == caseId
                  && w.WindowName.ToLower() == nameLower
                  && w.ClosedAt == null, ct);
        if (row is null) return false;
        ApplyClose(row, closedAt);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> RefreshStatesAsync(
        Guid caseId,
        DateTimeOffset asOf,
        CancellationToken ct = default)
    {
        var rows = await _db.Set<SlaWindow>()
            .Where(w => w.CaseId == caseId && w.ClosedAt == null)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;
        var atRiskFraction = _options.Value.AtRiskFraction;
        var changed = 0;
        foreach (var w in rows)
        {
            var newState = ComputeOpenState(w, asOf, atRiskFraction);
            if (w.State != newState)
            {
                w.State = newState;
                changed++;
            }
        }
        if (changed > 0)
            await _db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>
    /// Compute the lifecycle state for an OPEN window given a
    /// timestamp + the engine's at-risk threshold. Public so the
    /// dashboard query path can compute "what would the state be now?"
    /// without writing a row.
    /// </summary>
    public static SlaWindowState ComputeOpenState(SlaWindow window, DateTimeOffset asOf, double atRiskFraction)
    {
        if (asOf >= window.DueAt) return SlaWindowState.Breached;
        var elapsed = asOf - window.StartedAt;
        var total = window.DueAt - window.StartedAt;
        if (total <= TimeSpan.Zero) return SlaWindowState.OnTime;
        if (elapsed >= total * atRiskFraction) return SlaWindowState.AtRisk;
        return SlaWindowState.OnTime;
    }

    private static void ApplyClose(SlaWindow w, DateTimeOffset closedAt)
    {
        w.ClosedAt = closedAt;
        // Closed-after-due → Breached (preserves the breach signal); closed-before-due → Closed.
        w.State = closedAt > w.DueAt ? SlaWindowState.Breached : SlaWindowState.Closed;
    }
}

/// <summary>
/// Sprint 31 / B5.1 — engine defaults + thresholds for
/// <see cref="SlaTracker"/>. Per-tenant overrides live in
/// <c>tenancy.tenant_sla_settings</c>.
/// </summary>
public sealed class SlaTrackerOptions
{
    /// <summary>Bind path for IConfiguration consumers.</summary>
    public const string SectionName = "Inspection:Sla";

    /// <summary>
    /// Per-window-name budgets in minutes. Defaults below mirror the
    /// 30-day half-state CMR cycle on the slow end and a few-hour
    /// transit on the fast end. Override via configuration when
    /// pilot-site data tightens the budgets.
    /// </summary>
    public Dictionary<string, int> DefaultBudgets { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [SlaTracker.OpenToValidated] = 60,         // 1h to fetch documents
        [SlaTracker.ValidatedToVerdict] = 4 * 60,  // 4h analyst SLA
        [SlaTracker.VerdictToSubmitted] = 30       // 30m to submit downstream
    };

    /// <summary>
    /// Fraction of budget elapsed that flips <c>OnTime</c> to
    /// <c>AtRisk</c>. Default 0.5 (50%); range (0, 1).
    /// </summary>
    public double AtRiskFraction { get; set; } = 0.5;

    /// <summary>
    /// Fallback budget for window names that aren't in
    /// <see cref="DefaultBudgets"/> and don't have a per-tenant
    /// override. 24h is loose by design — a window opening with
    /// no configured budget shouldn't silently breach.
    /// </summary>
    public int FallbackBudgetMinutes { get; set; } = 24 * 60;
}
