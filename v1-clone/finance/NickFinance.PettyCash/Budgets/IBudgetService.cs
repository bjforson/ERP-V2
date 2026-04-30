using Microsoft.EntityFrameworkCore;

namespace NickFinance.PettyCash.Budgets;

/// <summary>
/// Module-facing API for petty-cash budget management. Budgets are caps
/// over a (scope, period); the service exposes utilisation queries and
/// a hook the petty-cash service calls after each disbursement to
/// increment consumed totals.
/// </summary>
public interface IBudgetService
{
    /// <summary>Persist a new budget. Caller is responsible for not creating overlapping budgets at the same scope.</summary>
    Task<Budget> CreateAsync(
        BudgetScope scope,
        string scopeKey,
        DateOnly periodStart,
        DateOnly periodEnd,
        long amountMinor,
        string currencyCode,
        Guid actorUserId,
        byte alertThresholdPct = 80,
        long tenantId = 1,
        CancellationToken ct = default);

    /// <summary>
    /// Apply a disbursed voucher against any matching budgets — by
    /// site (the float's site), category, AND site+category. Returns
    /// the budgets touched (with their NEW consumed totals) so the
    /// caller can decide if any alert threshold was crossed.
    /// </summary>
    Task<IReadOnlyList<Budget>> ApplyVoucherAsync(Voucher voucher, Guid siteId, CancellationToken ct = default);

    /// <summary>Get all budgets covering the given date for the tenant.</summary>
    Task<IReadOnlyList<Budget>> GetActiveAsync(DateOnly asOfDate, long tenantId = 1, CancellationToken ct = default);

    /// <summary>True if a budget would be exceeded by adding <paramref name="amountMinor"/> to its consumed total.</summary>
    Task<IReadOnlyList<BudgetAlert>> CheckAlertsAsync(IReadOnlyList<Budget> budgets, CancellationToken ct = default);
}

public sealed record BudgetAlert(
    Guid BudgetId,
    BudgetScope Scope,
    string ScopeKey,
    long AmountMinor,
    long ConsumedMinor,
    decimal UtilisationPct,
    AlertSeverity Severity);

public enum AlertSeverity
{
    /// <summary>Below the alert threshold — informational only.</summary>
    Ok = 0,

    /// <summary>At or above the alert threshold but still within budget — warn.</summary>
    Warning = 1,

    /// <summary>Consumed has exceeded the cap — block / escalate.</summary>
    Exceeded = 2
}

public sealed class BudgetService : IBudgetService
{
    private readonly PettyCashDbContext _db;
    private readonly TimeProvider _clock;

    public BudgetService(PettyCashDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<Budget> CreateAsync(
        BudgetScope scope,
        string scopeKey,
        DateOnly periodStart,
        DateOnly periodEnd,
        long amountMinor,
        string currencyCode,
        Guid actorUserId,
        byte alertThresholdPct = 80,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey)) throw new ArgumentException("ScopeKey is required.", nameof(scopeKey));
        if (amountMinor <= 0) throw new ArgumentException("Amount must be positive.", nameof(amountMinor));
        if (periodEnd < periodStart) throw new ArgumentException("PeriodEnd is before PeriodStart.", nameof(periodEnd));
        if (alertThresholdPct > 100) throw new ArgumentException("AlertThresholdPct must be 0..100.", nameof(alertThresholdPct));

        var b = new Budget
        {
            Scope = scope,
            ScopeKey = scopeKey,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            AmountMinor = amountMinor,
            ConsumedMinor = 0,
            CurrencyCode = currencyCode,
            AlertThresholdPct = alertThresholdPct,
            CreatedAt = _clock.GetUtcNow(),
            CreatedByUserId = actorUserId,
            TenantId = tenantId
        };
        _db.Budgets.Add(b);
        await _db.SaveChangesAsync(ct);
        return b;
    }

    public async Task<IReadOnlyList<Budget>> ApplyVoucherAsync(Voucher voucher, Guid siteId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        if (voucher.Status != VoucherStatus.Disbursed)
        {
            throw new PettyCashException("Budget consumption only applies to Disbursed vouchers.");
        }

        var amount = voucher.AmountApprovedMinor ?? voucher.AmountRequestedMinor;
        var date = voucher.DisbursedAt?.UtcDateTime.Date is { } d
            ? DateOnly.FromDateTime(d)
            : DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime.Date);

        var siteKey = siteId.ToString("N");
        var catKey = ((short)voucher.Category).ToString();
        var combinedKey = $"{siteKey}:{catKey}";

        var keys = new[] { siteKey, catKey, combinedKey };
        var matches = await _db.Budgets
            .Where(b =>
                b.TenantId == voucher.TenantId &&
                b.PeriodStart <= date &&
                b.PeriodEnd >= date &&
                b.CurrencyCode == voucher.CurrencyCode &&
                ((b.Scope == BudgetScope.Site && b.ScopeKey == siteKey) ||
                 (b.Scope == BudgetScope.Category && b.ScopeKey == catKey) ||
                 (b.Scope == BudgetScope.SiteCategory && b.ScopeKey == combinedKey)))
            .ToListAsync(ct);

        foreach (var b in matches)
        {
            b.ConsumedMinor = checked(b.ConsumedMinor + amount);
        }
        if (matches.Count > 0) await _db.SaveChangesAsync(ct);
        return matches;
    }

    public async Task<IReadOnlyList<Budget>> GetActiveAsync(DateOnly asOfDate, long tenantId = 1, CancellationToken ct = default)
    {
        return await _db.Budgets
            .Where(b => b.TenantId == tenantId && b.PeriodStart <= asOfDate && b.PeriodEnd >= asOfDate)
            .OrderBy(b => b.Scope).ThenBy(b => b.ScopeKey)
            .ToListAsync(ct);
    }

    public Task<IReadOnlyList<BudgetAlert>> CheckAlertsAsync(IReadOnlyList<Budget> budgets, CancellationToken ct = default)
    {
        var alerts = new List<BudgetAlert>(budgets.Count);
        foreach (var b in budgets)
        {
            var pct = b.AmountMinor == 0 ? 0m : (decimal)b.ConsumedMinor * 100m / b.AmountMinor;
            var sev = pct >= 100m ? AlertSeverity.Exceeded
                    : pct >= b.AlertThresholdPct ? AlertSeverity.Warning
                    : AlertSeverity.Ok;
            alerts.Add(new BudgetAlert(b.BudgetId, b.Scope, b.ScopeKey, b.AmountMinor, b.ConsumedMinor, pct, sev));
        }
        return Task.FromResult<IReadOnlyList<BudgetAlert>>(alerts);
    }
}
