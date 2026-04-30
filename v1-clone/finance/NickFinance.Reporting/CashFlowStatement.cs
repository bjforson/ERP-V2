using Microsoft.EntityFrameworkCore;
using NickFinance.Coa;
using NickFinance.Ledger;

namespace NickFinance.Reporting;

/// <summary>
/// Cash Flow Statement (indirect method). Starts with net P&amp;L, adjusts
/// for non-cash items (depreciation), then adjusts for changes in
/// working capital (AR, AP) over the period. Investing + financing
/// sections come from disposal / capital movements in the journal.
/// </summary>
public sealed record CashFlowStatementReport(
    DateOnly From,
    DateOnly To,
    string CurrencyCode,
    Money NetIncome,
    IReadOnlyList<CashFlowLine> Operating,
    IReadOnlyList<CashFlowLine> Investing,
    IReadOnlyList<CashFlowLine> Financing,
    Money OperatingNet,
    Money InvestingNet,
    Money FinancingNet,
    Money OpeningCash,
    Money ClosingCash);

public sealed record CashFlowLine(string Heading, Money Amount);

public static class CashFlowStatementBuilder
{
    /// <summary>The default cash-account codes considered "cash &amp; equivalents" for the CFS.</summary>
    public static readonly IReadOnlyList<string> CashAccounts = new[]
    {
        "1010", "1020", "1021", "1022",        // cash + MoMo wallets
        "1030", "1031", "1032", "1040",        // bank accounts
        "1060"                                  // petty cash float
    };

    public static async Task<CashFlowStatementReport> BuildAsync(
        LedgerDbContext ledger,
        IFinancialReports reports,
        string currencyCode,
        DateOnly from,
        DateOnly to,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(reports);

        var pnl = await reports.ProfitAndLossAsync(currencyCode, from, to, tenantId, ct);
        var netIncome = pnl.NetResult;

        // Working-capital changes over the period — derived from balance
        // changes on AR + AP control accounts.
        var arDelta = await BalanceDeltaAsync(ledger, "1100", currencyCode, from, to, tenantId, ct);
        var apDelta = await BalanceDeltaAsync(ledger, "2000", currencyCode, from, to, tenantId, ct);
        var depExpense = await PeriodDebitsAsync(ledger, "6700", currencyCode, from, to, tenantId, ct);

        // Operating section (indirect):
        //   Net income
        //   + Depreciation
        //   - Increase in AR (or + decrease)
        //   + Increase in AP (or - decrease)
        var operating = new List<CashFlowLine>
        {
            new("Net income", netIncome),
            new("Add: depreciation", new Money(depExpense, currencyCode)),
            new("Less: increase in trade receivables (1100)", new Money(-arDelta, currencyCode)),
            new("Add: increase in trade payables (2000)", new Money(apDelta, currencyCode))
        };
        var operatingNet = new Money(netIncome.Minor + depExpense - arDelta + apDelta, currencyCode);

        // Investing — fixed-asset cost movements (1500) + disposal proceeds.
        var ppeDelta = await BalanceDeltaAsync(ledger, "1500", currencyCode, from, to, tenantId, ct);
        var investing = new List<CashFlowLine>
        {
            new("Net change in property, plant & equipment (1500)", new Money(-ppeDelta, currencyCode))
        };
        var investingNet = new Money(-ppeDelta, currencyCode);

        // Financing — share capital (3000) + loans (2300/2310).
        var equityDelta = await BalanceDeltaAsync(ledger, "3000", currencyCode, from, to, tenantId, ct);
        var loanShortDelta = await BalanceDeltaAsync(ledger, "2300", currencyCode, from, to, tenantId, ct);
        var loanLongDelta = await BalanceDeltaAsync(ledger, "2310", currencyCode, from, to, tenantId, ct);
        var financing = new List<CashFlowLine>
        {
            new("Change in share capital (3000)", new Money(-equityDelta, currencyCode)),
            new("Change in short-term loans (2300)", new Money(loanShortDelta, currencyCode)),
            new("Change in long-term loans (2310)", new Money(loanLongDelta, currencyCode))
        };
        var financingNet = new Money(-equityDelta + loanShortDelta + loanLongDelta, currencyCode);

        // Opening + closing cash balances — sum of CashAccounts before/after.
        var openingCash = 0L;
        var closingCash = 0L;
        foreach (var code in CashAccounts)
        {
            var open = await BalanceAsAtAsync(ledger, code, currencyCode, from.AddDays(-1), tenantId, ct);
            var close = await BalanceAsAtAsync(ledger, code, currencyCode, to, tenantId, ct);
            openingCash += open;
            closingCash += close;
        }

        return new CashFlowStatementReport(
            from, to, currencyCode, netIncome,
            operating, investing, financing,
            operatingNet, investingNet, financingNet,
            new Money(openingCash, currencyCode),
            new Money(closingCash, currencyCode));
    }

    private static async Task<long> BalanceDeltaAsync(LedgerDbContext lg, string code, string ccy, DateOnly from, DateOnly to, long tenantId, CancellationToken ct)
    {
        var open = await BalanceAsAtAsync(lg, code, ccy, from.AddDays(-1), tenantId, ct);
        var close = await BalanceAsAtAsync(lg, code, ccy, to, tenantId, ct);
        return close - open;
    }

    private static async Task<long> BalanceAsAtAsync(LedgerDbContext lg, string code, string ccy, DateOnly asOf, long tenantId, CancellationToken ct)
    {
        var totals = await lg.EventLines
            .AsNoTracking()
            .Where(l => l.AccountCode == code && l.CurrencyCode == ccy
                     && l.Event.TenantId == tenantId
                     && l.Event.EffectiveDate <= asOf)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                D = g.Sum(x => (long?)x.DebitMinor) ?? 0L,
                C = g.Sum(x => (long?)x.CreditMinor) ?? 0L
            })
            .FirstOrDefaultAsync(ct);
        return (totals?.D ?? 0) - (totals?.C ?? 0);
    }

    private static async Task<long> PeriodDebitsAsync(LedgerDbContext lg, string code, string ccy, DateOnly from, DateOnly to, long tenantId, CancellationToken ct)
    {
        return await lg.EventLines
            .AsNoTracking()
            .Where(l => l.AccountCode == code && l.CurrencyCode == ccy
                     && l.Event.TenantId == tenantId
                     && l.Event.EffectiveDate >= from
                     && l.Event.EffectiveDate <= to)
            .SumAsync(l => (long?)l.DebitMinor, ct) ?? 0L;
    }
}
