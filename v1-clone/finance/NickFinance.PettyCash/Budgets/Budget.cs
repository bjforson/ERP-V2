namespace NickFinance.PettyCash.Budgets;

/// <summary>
/// One budget allocation for a (scope, period). Budgets are evaluated
/// after each disbursement; the consumed-amount is rolled forward as
/// vouchers Disburse against the matching scope.
/// </summary>
public class Budget
{
    public Guid BudgetId { get; set; } = Guid.NewGuid();

    public BudgetScope Scope { get; set; }

    /// <summary>
    /// Scope discriminator — for <see cref="BudgetScope.Site"/> this is the
    /// site id, for <see cref="BudgetScope.Category"/> the category enum
    /// short value, for <see cref="BudgetScope.SiteCategory"/> a composite
    /// of the two (<c>"{siteId:N}:{categoryShort}"</c>). Free-text up to
    /// 64 chars.
    /// </summary>
    public string ScopeKey { get; set; } = string.Empty;

    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>The cap, in minor units, for the period.</summary>
    public long AmountMinor { get; set; }

    /// <summary>Running consumed total — incremented by <see cref="IBudgetService"/> on each Disburse.</summary>
    public long ConsumedMinor { get; set; }

    public string CurrencyCode { get; set; } = "GHS";

    /// <summary>0..100 percent — UI / alert system warns when consumed/amount crosses this. Default 80%.</summary>
    public byte AlertThresholdPct { get; set; } = 80;

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public long TenantId { get; set; } = 1;
}

/// <summary>What a budget applies to.</summary>
public enum BudgetScope
{
    /// <summary>One site, all categories.</summary>
    Site = 1,

    /// <summary>One category, all sites.</summary>
    Category = 2,

    /// <summary>One (site, category) pair — the most specific.</summary>
    SiteCategory = 3
}
