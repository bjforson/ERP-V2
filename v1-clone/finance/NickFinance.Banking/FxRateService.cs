using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;

namespace NickFinance.Banking;

/// <summary>
/// Read-side FX conversion backed by the <c>banking.fx_rates</c> table.
/// Lookup chain:
/// <list type="number">
///   <item>Same currency → identity (returns amount unchanged, no DB hit).</item>
///   <item>Direct rate as of date — most recent row with as_of_date ≤ requested date.</item>
///   <item>Inverse rate fallback — if a USD→GHS row is missing but GHS→USD exists, use 1 / rate.</item>
///   <item>Throw <see cref="MissingFxRateException"/>.</item>
/// </list>
/// All conversion math is done in <see cref="decimal"/> on minor units; the
/// result is rounded half-to-even to whole minor units. Banker's rounding
/// matches the kernel's <see cref="Money.MultiplyRate"/> behaviour so a Money
/// converted via this service, then converted back, ends up cent-stable.
/// </summary>
public sealed class FxRateService : IFxConverter
{
    private readonly BankingDbContext _db;

    public FxRateService(BankingDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<decimal?> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        DateOnly asOf,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCurrency);
        ArgumentException.ThrowIfNullOrWhiteSpace(toCurrency);

        var from = fromCurrency.Trim().ToUpperInvariant();
        var to = toCurrency.Trim().ToUpperInvariant();

        if (string.Equals(from, to, StringComparison.Ordinal)) return 1m;

        // Direct rate — latest as_of_date ≤ requested date.
        var direct = await _db.FxRates
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && r.FromCurrency == from
                     && r.ToCurrency == to
                     && r.AsOfDate <= asOf)
            .OrderByDescending(r => r.AsOfDate)
            .Select(r => (decimal?)r.Rate)
            .FirstOrDefaultAsync(ct);
        if (direct.HasValue) return direct;

        // Inverse rate — if we have GHS→USD = 0.0617 and someone asks for USD→GHS,
        // 1/0.0617 ≈ 16.207. Caller doesn't have to seed both directions.
        var inverse = await _db.FxRates
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && r.FromCurrency == to
                     && r.ToCurrency == from
                     && r.AsOfDate <= asOf)
            .OrderByDescending(r => r.AsOfDate)
            .Select(r => (decimal?)r.Rate)
            .FirstOrDefaultAsync(ct);
        if (inverse.HasValue && inverse.Value != 0m) return 1m / inverse.Value;

        return null;
    }

    public async Task<Money> ConvertAsync(
        Money amount,
        string toCurrency,
        DateOnly asOf,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toCurrency);

        var to = toCurrency.Trim().ToUpperInvariant();
        if (string.Equals(amount.CurrencyCode, to, StringComparison.Ordinal)) return amount;

        var rate = await GetRateAsync(amount.CurrencyCode, to, asOf, tenantId, ct).ConfigureAwait(false)
                   ?? throw new MissingFxRateException(amount.CurrencyCode, to, asOf, tenantId);

        // Banker's rounding to whole minor units. We multiply minor (an integer
        // count) by the rate (a decimal) — the product is decimal, then we
        // round to 0 dp before casting back to long.
        var converted = decimal.Round(amount.Minor * rate, 0, MidpointRounding.ToEven);
        return new Money((long)converted, to);
    }
}
