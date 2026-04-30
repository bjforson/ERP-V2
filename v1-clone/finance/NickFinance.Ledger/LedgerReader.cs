using Microsoft.EntityFrameworkCore;

namespace NickFinance.Ledger;

/// <summary>
/// Read-side projections over ledger_events. Kept deliberately small for v1
/// — account balance and trial balance are what petty cash needs. AR aging,
/// AP aging, etc. are projections for the module that owns them.
/// </summary>
public interface ILedgerReader
{
    /// <summary>Sum of DR - CR for an account up to and including the given date.</summary>
    Task<Money> GetAccountBalanceAsync(
        string accountCode,
        string currencyCode,
        DateOnly asOf,
        long tenantId = 1,
        CancellationToken ct = default);

    /// <summary>
    /// Trial balance: for every account with any activity, its total debits,
    /// total credits, and net balance.
    /// </summary>
    Task<IReadOnlyList<TrialBalanceRow>> GetTrialBalanceAsync(
        string currencyCode,
        DateOnly asOf,
        long tenantId = 1,
        CancellationToken ct = default);

    /// <summary>
    /// Per-currency net balance (DR-CR) for a single account up to <paramref name="asOf"/>.
    /// Returns one row per non-zero currency the account has activity in. The list
    /// excludes currencies that net to zero. Used by <c>IFxRevaluationService</c>
    /// to find every foreign-currency exposure that needs translating.
    /// </summary>
    Task<IReadOnlyList<(string CurrencyCode, long BalanceMinor)>> GetAccountBalancesByCurrencyAsync(
        string accountCode,
        DateOnly asOf,
        long tenantId = 1,
        CancellationToken ct = default);
}

public record TrialBalanceRow(
    string AccountCode,
    Money Debits,
    Money Credits,
    Money Balance);

public class LedgerReader : ILedgerReader
{
    private readonly LedgerDbContext _db;

    public LedgerReader(LedgerDbContext db) => _db = db;

    public async Task<Money> GetAccountBalanceAsync(
        string accountCode,
        string currencyCode,
        DateOnly asOf,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        // Reversal events are regular events with their legs already flipped —
        // summing across Posted + Reversal gives net correctly with no special-casing.
        var result = await _db.EventLines
            .AsNoTracking()
            .Where(l => l.AccountCode == accountCode
                     && l.CurrencyCode == currencyCode
                     && l.Event.TenantId == tenantId
                     && l.Event.EffectiveDate <= asOf)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Debits = g.Sum(x => (long?)x.DebitMinor) ?? 0L,
                Credits = g.Sum(x => (long?)x.CreditMinor) ?? 0L
            })
            .FirstOrDefaultAsync(ct);

        var debits = result?.Debits ?? 0L;
        var credits = result?.Credits ?? 0L;
        return new Money(debits - credits, currencyCode);
    }

    public async Task<IReadOnlyList<(string CurrencyCode, long BalanceMinor)>> GetAccountBalancesByCurrencyAsync(
        string accountCode,
        DateOnly asOf,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        var rows = await _db.EventLines
            .AsNoTracking()
            .Where(l => l.AccountCode == accountCode
                     && l.Event.TenantId == tenantId
                     && l.Event.EffectiveDate <= asOf)
            .GroupBy(l => l.CurrencyCode)
            .Select(g => new
            {
                Currency = g.Key,
                Debits = g.Sum(x => (long?)x.DebitMinor) ?? 0L,
                Credits = g.Sum(x => (long?)x.CreditMinor) ?? 0L
            })
            .ToListAsync(ct);

        return rows
            .Select(r => (CurrencyCode: r.Currency, BalanceMinor: r.Debits - r.Credits))
            .Where(x => x.BalanceMinor != 0)
            .OrderBy(x => x.CurrencyCode, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<TrialBalanceRow>> GetTrialBalanceAsync(
        string currencyCode,
        DateOnly asOf,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        var rows = await _db.EventLines
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

        return rows.Select(r => new TrialBalanceRow(
            r.AccountCode,
            new Money(r.Debits, currencyCode),
            new Money(r.Credits, currencyCode),
            new Money(r.Debits - r.Credits, currencyCode))).ToList();
    }
}
