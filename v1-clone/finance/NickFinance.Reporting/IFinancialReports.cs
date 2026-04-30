using Microsoft.EntityFrameworkCore;
using NickFinance.Coa;
using NickFinance.Ledger;

namespace NickFinance.Reporting;

/// <summary>
/// Read-only financial report queries. All reports take an <c>asOf</c> date
/// (and where relevant a <c>from</c> date) and a tenant; nothing here
/// caches or denormalises — just SQL-able LINQ over <c>finance.*</c>.
/// </summary>
public interface IFinancialReports
{
    /// <summary>Trial balance — every account with any activity, with totals and net balance.</summary>
    Task<TrialBalanceReport> TrialBalanceAsync(string currencyCode, DateOnly asOf, long tenantId = 1, CancellationToken ct = default);

    /// <summary>Balance sheet — Assets, Liabilities, Equity grouped by <see cref="AccountType"/>.</summary>
    Task<BalanceSheetReport> BalanceSheetAsync(string currencyCode, DateOnly asOf, long tenantId = 1, CancellationToken ct = default);

    /// <summary>Profit &amp; Loss — Income vs Expenses for a date range, with net result.</summary>
    Task<ProfitAndLossReport> ProfitAndLossAsync(string currencyCode, DateOnly from, DateOnly to, long tenantId = 1, CancellationToken ct = default);

    /// <summary>Detailed list of every ledger line for a single account in a date range.</summary>
    Task<GlDetailReport> GlDetailAsync(string accountCode, string currencyCode, DateOnly from, DateOnly to, long tenantId = 1, CancellationToken ct = default);

    /// <summary>
    /// Trial balance with foreign-currency balances translated into
    /// <paramref name="targetCurrency"/> at the rate as of <paramref name="asOf"/>.
    /// Each account's per-currency net is converted via
    /// <paramref name="converter"/> and summed; the result is one row per
    /// account in the target currency. Wave 3A FX Phase 2 — pairs with
    /// <c>IFxRevaluationService</c> as the read-side complement of the
    /// revaluation write path.
    /// </summary>
    Task<TrialBalanceReport> RevaluedTrialBalanceAsync(
        string targetCurrency,
        DateOnly asOf,
        IFxConverter converter,
        long tenantId = 1,
        CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Report shapes
// ---------------------------------------------------------------------------

public sealed record TrialBalanceReport(
    DateOnly AsOf,
    string CurrencyCode,
    IReadOnlyList<TrialBalanceRow> Rows,
    Money TotalDebits,
    Money TotalCredits)
{
    /// <summary>True if total debits == total credits — the GL invariant the kernel enforces. Should always be true.</summary>
    public bool IsBalanced => TotalDebits.Minor == TotalCredits.Minor;
}

public sealed record TrialBalanceRow(
    string AccountCode,
    string? AccountName,
    AccountType? AccountType,
    Money Debits,
    Money Credits,
    Money Balance);

public sealed record BalanceSheetReport(
    DateOnly AsOf,
    string CurrencyCode,
    IReadOnlyList<BalanceSheetSection> Sections,
    Money TotalAssets,
    Money TotalLiabilitiesAndEquity)
{
    /// <summary>True when the balance sheet equation holds: Assets = Liabilities + Equity.</summary>
    public bool IsBalanced => TotalAssets.Minor == TotalLiabilitiesAndEquity.Minor;
}

public sealed record BalanceSheetSection(
    AccountType Type,
    string Heading,
    IReadOnlyList<BalanceSheetLine> Lines,
    Money SectionTotal);

public sealed record BalanceSheetLine(
    string AccountCode,
    string? AccountName,
    Money Balance);

public sealed record ProfitAndLossReport(
    DateOnly From,
    DateOnly To,
    string CurrencyCode,
    IReadOnlyList<BalanceSheetLine> Income,
    IReadOnlyList<BalanceSheetLine> Expenses,
    Money TotalIncome,
    Money TotalExpenses,
    Money NetResult);

public sealed record GlDetailReport(
    string AccountCode,
    string CurrencyCode,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<GlDetailRow> Rows,
    Money OpeningBalance,
    Money ClosingBalance);

public sealed record GlDetailRow(
    DateOnly Date,
    Guid EventId,
    string SourceModule,
    string SourceEntityId,
    string Narration,
    Money DebitAmount,
    Money CreditAmount,
    Money RunningBalance);
