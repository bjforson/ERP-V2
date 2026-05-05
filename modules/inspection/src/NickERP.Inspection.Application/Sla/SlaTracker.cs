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

    public Task<IReadOnlyList<SlaWindow>> OpenWindowsAsync(
        Guid caseId,
        IReadOnlyCollection<string> windowNames,
        DateTimeOffset openedAt,
        CancellationToken ct = default)
        => OpenWindowsAsync(caseId, windowNames, openedAt, QueueTier.Standard, ct);

    /// <summary>
    /// Sprint 45 / Phase C — open windows under a specific
    /// <see cref="QueueTier"/>. Budget resolution order:
    /// <list type="number">
    ///   <item>Per-tenant per-window override (existing path).</item>
    ///   <item>Per-tier first-review / final budget when the window name matches a known tier-bound window.</item>
    ///   <item>Engine default budget.</item>
    ///   <item>Fallback budget.</item>
    /// </list>
    /// Per-tier defaults: Standard 15m / 60m, High 5m / 30m, Urgent
    /// 1m / 10m, PostClearance 24h, Exception indefinite (no
    /// deadline; window is created with a far-future DueAt to keep
    /// the unique-index posture intact).
    /// </summary>
    public async Task<IReadOnlyList<SlaWindow>> OpenWindowsAsync(
        Guid caseId,
        IReadOnlyCollection<string> windowNames,
        DateTimeOffset openedAt,
        QueueTier tier,
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
        var tierBudget = ResolveTierBudget(tier, _options.Value);
        var opened = new List<SlaWindow>(windowNames.Count);
        foreach (var name in windowNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existingByName.TryGetValue(name, out var prior))
            {
                opened.Add(prior);
                continue;
            }

            // Per-tenant override is always authoritative. Otherwise,
            // tier-aware resolution kicks in:
            //   - Standard tier: engine per-window default →
            //     per-tier (15m / 60m) → fallback. Preserves the
            //     pre-Sprint-45 Standard-tier contract for hosts
            //     that haven't migrated their engine defaults.
            //   - Non-Standard tier (High / Urgent / Exception /
            //     PostClearance): per-tier budget → engine default
            //     → fallback. The tier explicitly requested by the
            //     caller takes precedence over the legacy engine
            //     default — that's the whole point of the tier.
            int budget;
            if (settings.TryGetValue(name, out var snap))
            {
                if (!snap.Enabled) continue; // tenant disabled this window
                budget = snap.TargetMinutes;
            }
            else if (tier == QueueTier.Standard)
            {
                if (defaults.TryGetValue(name, out var def))
                {
                    budget = def;
                }
                else if (TryResolveTierWindowBudget(tierBudget, name, out var tierMinutes))
                {
                    budget = tierMinutes;
                }
                else
                {
                    budget = _options.Value.FallbackBudgetMinutes;
                }
            }
            else
            {
                if (TryResolveTierWindowBudget(tierBudget, name, out var tierMinutes))
                {
                    budget = tierMinutes;
                }
                else if (IsTierIndefinite(tierBudget) && IsTierBoundWindow(name))
                {
                    // Tier explicitly requested an indefinite budget
                    // (0/0) for a tier-bound window — caller wants no
                    // SLA enforcement (Exception tier). Skip the row
                    // rather than falling through to engine defaults
                    // — that would silently re-impose a deadline the
                    // tier deliberately suppressed.
                    continue;
                }
                else if (defaults.TryGetValue(name, out var def))
                {
                    budget = def;
                }
                else
                {
                    budget = _options.Value.FallbackBudgetMinutes;
                }
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
                QueueTier = tier,
                QueueTierIsManual = false,
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
        => OpenWindowsAsync(caseId, StandardWindows, openedAt, QueueTier.Standard, ct);

    /// <summary>
    /// Sprint 45 / Phase C — resolve the (firstReviewMinutes,
    /// finalMinutes) pair for the given <paramref name="tier"/>.
    /// Reads per-tenant overrides from
    /// <see cref="SlaTrackerOptions.TierOverrides"/>; falls back to
    /// the hard-coded tier defaults
    /// <see cref="SlaTrackerOptions.TierDefaults"/>.
    /// Public so tests + the dashboard can inspect what the active
    /// tier budget would be for a given configuration.
    /// </summary>
    public static (int FirstReviewMinutes, int FinalMinutes) ResolveTierBudget(
        QueueTier tier, SlaTrackerOptions options)
    {
        if (options.TierOverrides.TryGetValue(tier, out var ov))
        {
            return ov;
        }
        if (options.TierDefaults.TryGetValue(tier, out var def))
        {
            return def;
        }
        // Unknown tier — fall back to Standard.
        return options.TierDefaults[QueueTier.Standard];
    }

    /// <summary>
    /// Sprint 45 / Phase C — true when both (firstReview, final) are
    /// 0 (the Exception-tier sentinel). Used to suppress engine-default
    /// fallback when the caller explicitly requested no SLA enforcement.
    /// </summary>
    private static bool IsTierIndefinite((int FirstReviewMinutes, int FinalMinutes) tier)
        => tier.FirstReviewMinutes <= 0 && tier.FinalMinutes <= 0;

    /// <summary>
    /// Sprint 45 / Phase C — true when the window name is one of the
    /// well-known tier-bound windows (case.open_to_validated /
    /// case.validated_to_verdict / case.verdict_to_submitted). Used
    /// to gate the Exception-tier no-fallback branch — non-tier-bound
    /// windows still get engine defaults under any tier.
    /// </summary>
    private static bool IsTierBoundWindow(string windowName)
        => string.Equals(windowName, OpenToValidated, StringComparison.OrdinalIgnoreCase)
        || string.Equals(windowName, ValidatedToVerdict, StringComparison.OrdinalIgnoreCase)
        || string.Equals(windowName, VerdictToSubmitted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sprint 45 / Phase C — for a "well-known" SLA window name,
    /// pluck the per-tier budget. Returns false when the window name
    /// isn't tier-bound (caller falls back to engine defaults).
    /// </summary>
    private static bool TryResolveTierWindowBudget(
        (int FirstReviewMinutes, int FinalMinutes) tier,
        string windowName,
        out int minutes)
    {
        // First-review windows: case.open_to_validated.
        if (string.Equals(windowName, OpenToValidated, StringComparison.OrdinalIgnoreCase))
        {
            minutes = tier.FirstReviewMinutes;
            return minutes > 0;
        }
        // Final windows: validated→verdict + verdict→submitted both
        // get the "final" budget. This matches the pilot interpretation
        // that "final" is the time from open to fully-submitted, and
        // we split it across the two later phases proportionally.
        if (string.Equals(windowName, ValidatedToVerdict, StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowName, VerdictToSubmitted, StringComparison.OrdinalIgnoreCase))
        {
            minutes = tier.FinalMinutes;
            return minutes > 0;
        }
        minutes = 0;
        return false;
    }

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

    /// <summary>
    /// Sprint 45 / Phase C — hard-coded per-tier (first-review, final)
    /// budget defaults. Resolution order in
    /// <see cref="SlaTracker.OpenWindowsAsync(Guid, IReadOnlyCollection{string}, DateTimeOffset, NickERP.Inspection.Core.Entities.QueueTier, CancellationToken)"/>:
    /// per-tenant per-window override → per-tenant per-tier override
    /// (from <see cref="TierOverrides"/>) → these defaults → engine
    /// per-window defaults → fallback.
    /// </summary>
    public Dictionary<NickERP.Inspection.Core.Entities.QueueTier, (int FirstReviewMinutes, int FinalMinutes)> TierDefaults { get; set; } =
        new()
        {
            [NickERP.Inspection.Core.Entities.QueueTier.Standard] = (15, 60),
            [NickERP.Inspection.Core.Entities.QueueTier.High] = (5, 30),
            [NickERP.Inspection.Core.Entities.QueueTier.Urgent] = (1, 10),
            // Exception: indefinite — windows opened under this tier
            // have no enforced deadline. The caller may still pass an
            // explicit per-window budget; otherwise the windows
            // become open-ended (DueAt set to a far-future sentinel
            // by the unique-index posture).
            [NickERP.Inspection.Core.Entities.QueueTier.Exception] = (0, 0),
            [NickERP.Inspection.Core.Entities.QueueTier.PostClearance] = (24 * 60, 24 * 60)
        };

    /// <summary>
    /// Sprint 45 / Phase C — per-tenant tier-override slot. The
    /// host fills this from <c>TenantSetting</c> rows
    /// (e.g. <c>inspection.queue.standard_first_review_minutes</c>);
    /// the SlaTracker treats it as a transparent dictionary lookup
    /// when the tier is non-default.
    /// </summary>
    public Dictionary<NickERP.Inspection.Core.Entities.QueueTier, (int FirstReviewMinutes, int FinalMinutes)> TierOverrides { get; set; } =
        new();
}
