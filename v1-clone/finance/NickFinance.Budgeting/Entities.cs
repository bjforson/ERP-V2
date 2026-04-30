namespace NickFinance.Budgeting;

/// <summary>
/// One annual budget header — covers a fiscal year for a tenant + an
/// optional department dimension. The <see cref="BudgetLine"/> rows
/// carry per-month amounts per GL account.
/// </summary>
public class AnnualBudget
{
    public Guid AnnualBudgetId { get; set; } = Guid.NewGuid();
    public int FiscalYear { get; set; }
    public string? DepartmentCode { get; set; }   // null = whole-entity budget
    public string Name { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = "GHS";
    public BudgetStatus Status { get; set; } = BudgetStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public long TenantId { get; set; } = 1;
    public List<BudgetLine> Lines { get; set; } = new();
}

public enum BudgetStatus
{
    Draft = 0,
    Approved = 1,
    Closed = 2
}

/// <summary>One line of an annual budget: GL account × month × amount.</summary>
public class BudgetLine
{
    public Guid BudgetLineId { get; set; } = Guid.NewGuid();
    public Guid AnnualBudgetId { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    /// <summary>Month index, 1-12.</summary>
    public byte MonthNumber { get; set; }
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "GHS";
}

/// <summary>Variance row produced by <see cref="IBudgetingService.VarianceAsync"/> — budget vs actual for one (account, month).</summary>
public sealed record BudgetVarianceRow(
    string AccountCode,
    byte MonthNumber,
    long BudgetMinor,
    long ActualMinor,
    long VarianceMinor,
    decimal VariancePct);

/// <summary>One row of a 13-week cash forecast.</summary>
public sealed record CashForecastRow(
    DateOnly WeekStart,
    long ExpectedReceiptsMinor,
    long ExpectedPaymentsMinor,
    long NetMinor);
