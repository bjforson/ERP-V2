using Microsoft.EntityFrameworkCore;
using NickFinance.Coa;
using NickFinance.Ledger;

namespace NickFinance.Reporting;

/// <summary>
/// Default implementation. Pulls from both <see cref="LedgerDbContext"/>
/// (the journals) and <see cref="CoaDbContext"/> (the names + types).
/// Nothing fancy — pure GROUP BY queries that Postgres handles natively.
/// </summary>
public sealed class FinancialReports : IFinancialReports
{
    private readonly LedgerDbContext _ledger;
    private readonly CoaDbContext? _coa;

    public FinancialReports(LedgerDbContext ledger, CoaDbContext? coa = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _coa = coa;
    }

    // -----------------------------------------------------------------
    // Trial balance
    // -----------------------------------------------------------------

    public async Task<TrialBalanceReport> TrialBalanceAsync(string currencyCode, DateOnly asOf, long tenantId = 1, CancellationToken ct = default)
    {
        var raw = await _ledger.EventLines
            .AsNoTracking()
            .Where(l => l.CurrencyCode == currencyCode
                     && l.Event.TenantId == tenantId
                     && l.Event.EffectiveDate <= asOf)
            .GroupBy(l => l.AccountCode)
            .Select(g => new
            {
                AccountCode = g.Key,
                Debits = g.Sum(x => (long?)x.DebitMinor) ?? 0L,
                Credits = g.Sum(x => (long?)x.CreditMinor) ?? 0L
            })
            .OrderBy(r => r.AccountCode)
            .ToListAsync(ct);

        var meta = await LookupMetaAsync(raw.Select(r => r.AccountCode).ToList(), tenantId, ct);

        var rows = raw.Select(r =>
        {
            meta.TryGetValue(r.AccountCode, out var m);
            return new TrialBalanceRow(
                AccountCode: r.AccountCode,
                AccountName: m?.Name,
                AccountType: m?.Type,
                Debits: new Money(r.Debits, currencyCode),
                Credits: new Money(r.Credits, currencyCode),
                Balance: new Money(r.Debits - r.Credits, currencyCode));
        }).ToList();

        var totalDr = new Money(raw.Sum(r => r.Debits), currencyCode);
        var totalCr = new Money(raw.Sum(r => r.Credits), currencyCode);
        return new TrialBalanceReport(asOf, currencyCode, rows, totalDr, totalCr);
    }

    // -----------------------------------------------------------------
    // Balance sheet
    // -----------------------------------------------------------------

    public async Task<BalanceSheetReport> BalanceSheetAsync(string currencyCode, DateOnly asOf, long tenantId = 1, CancellationToken ct = default)
    {
        var tb = await TrialBalanceAsync(currencyCode, asOf, tenantId, ct);

        // Roll closing P&L into Equity per accounting convention.
        var fromYearStart = new DateOnly(asOf.Year, 1, 1);
        var pnl = await ProfitAndLossAsync(currencyCode, fromYearStart, asOf, tenantId, ct);

        var assets = BuildSection(tb, AccountType.Asset, "Assets",
            // Asset balance is DR positive (debits-credits)
            row => row.Balance);
        var liabilities = BuildSection(tb, AccountType.Liability, "Liabilities",
            // Liability balance is CR positive — flip sign so it shows positive
            row => row.Balance.Negate());
        var equity = BuildSection(tb, AccountType.Equity, "Equity",
            row => row.Balance.Negate(), pnlNetResult: pnl.NetResult);

        var totalAssets = SumLines(assets.Lines, currencyCode);
        var totalLiabAndEquity = SumLines(liabilities.Lines, currencyCode).Add(SumLines(equity.Lines, currencyCode));

        return new BalanceSheetReport(asOf, currencyCode,
            new[] { assets, liabilities, equity },
            totalAssets, totalLiabAndEquity);
    }

    private static BalanceSheetSection BuildSection(
        TrialBalanceReport tb, AccountType type, string heading,
        Func<TrialBalanceRow, Money> presentBalance,
        Money? pnlNetResult = null)
    {
        var rows = tb.Rows
            .Where(r => r.AccountType == type)
            .Select(r => new BalanceSheetLine(r.AccountCode, r.AccountName, presentBalance(r)))
            .Where(l => !l.Balance.IsZero)
            .OrderBy(l => l.AccountCode)
            .ToList();
        // Append YTD P&L into Equity as a synthesised line.
        if (type == AccountType.Equity && pnlNetResult is not null && !pnlNetResult.Value.IsZero)
        {
            rows = rows.Concat(new[]
            {
                new BalanceSheetLine("YTD", "Current-year retained earnings (YTD)", pnlNetResult.Value)
            }).ToList();
        }

        var ccy = tb.CurrencyCode;
        var sectionTotal = SumLines(rows, ccy);
        return new BalanceSheetSection(type, heading, rows, sectionTotal);
    }

    private static Money SumLines(IReadOnlyList<BalanceSheetLine> lines, string ccy)
    {
        var total = Money.Zero(ccy);
        foreach (var l in lines) total = total.Add(l.Balance);
        return total;
    }

    // -----------------------------------------------------------------
    // P&L
    // -----------------------------------------------------------------

    public async Task<ProfitAndLossReport> ProfitAndLossAsync(string currencyCode, DateOnly from, DateOnly to, long tenantId = 1, CancellationToken ct = default)
    {
        var raw = await _ledger.EventLines
            .AsNoTracking()
            .Where(l => l.CurrencyCode == currencyCode
                     && l.Event.TenantId == tenantId
                     && l.Event.EffectiveDate >= from
                     && l.Event.EffectiveDate <= to)
            .GroupBy(l => l.AccountCode)
            .Select(g => new
            {
                AccountCode = g.Key,
                Debits = g.Sum(x => (long?)x.DebitMinor) ?? 0L,
                Credits = g.Sum(x => (long?)x.CreditMinor) ?? 0L
            })
            .ToListAsync(ct);

        var meta = await LookupMetaAsync(raw.Select(r => r.AccountCode).ToList(), tenantId, ct);

        var income = new List<BalanceSheetLine>();
        var expenses = new List<BalanceSheetLine>();
        foreach (var r in raw)
        {
            meta.TryGetValue(r.AccountCode, out var m);
            if (m is null) continue;
            // Income normal-balance is credit — present positive.
            if (m.Type == AccountType.Income)
            {
                income.Add(new BalanceSheetLine(r.AccountCode, m.Name, new Money(r.Credits - r.Debits, currencyCode)));
            }
            // Expense normal-balance is debit — present positive.
            else if (m.Type == AccountType.Expense)
            {
                expenses.Add(new BalanceSheetLine(r.AccountCode, m.Name, new Money(r.Debits - r.Credits, currencyCode)));
            }
        }
        income = income.Where(l => !l.Balance.IsZero).OrderBy(l => l.AccountCode).ToList();
        expenses = expenses.Where(l => !l.Balance.IsZero).OrderBy(l => l.AccountCode).ToList();

        var totalIncome = SumLines(income, currencyCode);
        var totalExpenses = SumLines(expenses, currencyCode);
        var net = totalIncome.Subtract(totalExpenses);
        return new ProfitAndLossReport(from, to, currencyCode, income, expenses, totalIncome, totalExpenses, net);
    }

    // -----------------------------------------------------------------
    // Revalued trial balance
    // -----------------------------------------------------------------

    public async Task<TrialBalanceReport> RevaluedTrialBalanceAsync(
        string targetCurrency,
        DateOnly asOf,
        IFxConverter converter,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCurrency);
        ArgumentNullException.ThrowIfNull(converter);

        // Pull every (account, currency, dr, cr) tuple — one row per
        // currency the account has activity in. We translate each into the
        // target currency, then group by account.
        var raw = await _ledger.EventLines
            .AsNoTracking()
            .Where(l => l.Event.TenantId == tenantId && l.Event.EffectiveDate <= asOf)
            .GroupBy(l => new { l.AccountCode, l.CurrencyCode })
            .Select(g => new
            {
                g.Key.AccountCode,
                g.Key.CurrencyCode,
                Debits = g.Sum(x => (long?)x.DebitMinor) ?? 0L,
                Credits = g.Sum(x => (long?)x.CreditMinor) ?? 0L
            })
            .ToListAsync(ct);

        var meta = await LookupMetaAsync(raw.Select(r => r.AccountCode).Distinct().ToList(), tenantId, ct);

        // Translate every per-currency total into the target currency.
        // ConvertAsync returns the target-currency Money; banker's rounding
        // is consistent with the rest of the FX pipeline.
        var byAccount = new Dictionary<string, (long Debits, long Credits)>(StringComparer.Ordinal);
        foreach (var r in raw)
        {
            var dr = await converter.ConvertAsync(new Money(r.Debits, r.CurrencyCode), targetCurrency, asOf, tenantId, ct);
            var cr = await converter.ConvertAsync(new Money(r.Credits, r.CurrencyCode), targetCurrency, asOf, tenantId, ct);
            if (!byAccount.TryGetValue(r.AccountCode, out var existing)) existing = (0L, 0L);
            byAccount[r.AccountCode] = (existing.Debits + dr.Minor, existing.Credits + cr.Minor);
        }

        var rows = byAccount
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                meta.TryGetValue(kv.Key, out var m);
                return new TrialBalanceRow(
                    AccountCode: kv.Key,
                    AccountName: m?.Name,
                    AccountType: m?.Type,
                    Debits: new Money(kv.Value.Debits, targetCurrency),
                    Credits: new Money(kv.Value.Credits, targetCurrency),
                    Balance: new Money(kv.Value.Debits - kv.Value.Credits, targetCurrency));
            })
            .ToList();

        var totalDr = new Money(byAccount.Values.Sum(v => v.Debits), targetCurrency);
        var totalCr = new Money(byAccount.Values.Sum(v => v.Credits), targetCurrency);
        return new TrialBalanceReport(asOf, targetCurrency, rows, totalDr, totalCr);
    }

    // -----------------------------------------------------------------
    // GL detail
    // -----------------------------------------------------------------

    public async Task<GlDetailReport> GlDetailAsync(string accountCode, string currencyCode, DateOnly from, DateOnly to, long tenantId = 1, CancellationToken ct = default)
    {
        // Opening balance = sum dr-cr strictly before `from`.
        var openingRaw = await _ledger.EventLines
            .AsNoTracking()
            .Where(l => l.AccountCode == accountCode
                     && l.CurrencyCode == currencyCode
                     && l.Event.TenantId == tenantId
                     && l.Event.EffectiveDate < from)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Debits = g.Sum(x => (long?)x.DebitMinor) ?? 0L,
                Credits = g.Sum(x => (long?)x.CreditMinor) ?? 0L
            })
            .FirstOrDefaultAsync(ct);
        var opening = new Money((openingRaw?.Debits ?? 0) - (openingRaw?.Credits ?? 0), currencyCode);

        var lines = await _ledger.EventLines
            .AsNoTracking()
            .Include(l => l.Event)
            .Where(l => l.AccountCode == accountCode
                     && l.CurrencyCode == currencyCode
                     && l.Event.TenantId == tenantId
                     && l.Event.EffectiveDate >= from
                     && l.Event.EffectiveDate <= to)
            .OrderBy(l => l.Event.EffectiveDate)
            .ThenBy(l => l.Event.CommittedAt)
            .ThenBy(l => l.LineNo)
            .ToListAsync(ct);

        var rows = new List<GlDetailRow>(lines.Count);
        var running = opening.Minor;
        foreach (var l in lines)
        {
            running = running + l.DebitMinor - l.CreditMinor;
            rows.Add(new GlDetailRow(
                Date: l.Event.EffectiveDate,
                EventId: l.EventId,
                SourceModule: l.Event.SourceModule,
                SourceEntityId: l.Event.SourceEntityId,
                Narration: l.Event.Narration,
                DebitAmount: new Money(l.DebitMinor, currencyCode),
                CreditAmount: new Money(l.CreditMinor, currencyCode),
                RunningBalance: new Money(running, currencyCode)));
        }
        return new GlDetailReport(accountCode, currencyCode, from, to, rows, opening, new Money(running, currencyCode));
    }

    // -----------------------------------------------------------------
    // Metadata lookup helper
    // -----------------------------------------------------------------

    private async Task<Dictionary<string, AccountMeta>> LookupMetaAsync(List<string> codes, long tenantId, CancellationToken ct)
    {
        if (_coa is null || codes.Count == 0)
        {
            return new Dictionary<string, AccountMeta>(StringComparer.Ordinal);
        }
        var rows = await _coa.Accounts.AsNoTracking()
            .Where(a => a.TenantId == tenantId && codes.Contains(a.Code))
            .Select(a => new { a.Code, a.Name, a.Type })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Code, r => new AccountMeta(r.Name, r.Type), StringComparer.Ordinal);
    }

    private sealed record AccountMeta(string Name, AccountType Type);
}
