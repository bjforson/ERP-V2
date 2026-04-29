using Microsoft.EntityFrameworkCore;
using NickERP.NickFinance.Core.Entities;
using NickERP.NickFinance.Core.Services;

namespace NickERP.NickFinance.Database.Services;

/// <summary>
/// EF-backed implementation of <see cref="IFxRateLookup"/> — see G2 §1.10.
///
/// <para>
/// Resolution rule: the latest row whose
/// <see cref="FxRate.EffectiveDate"/> is on-or-before the requested
/// date. Identity pairs (USD→USD) short-circuit to a synthetic 1.0 row
/// without hitting the DB.
/// </para>
///
/// <para>
/// Reads <c>fx_rate</c> directly through the
/// <see cref="NickFinanceDbContext"/>. The table opts in to
/// <c>app.tenant_id = '-1'</c> for system-context inserts; reads work
/// from any session because the OR clause on the policy is permissive.
/// </para>
/// </summary>
public sealed class FxRateLookup : IFxRateLookup
{
    private readonly NickFinanceDbContext _db;

    public FxRateLookup(NickFinanceDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public async Task<FxRate?> ResolveAsync(
        string fromCurrency,
        string toCurrency,
        DateTime effectiveDate,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCurrency);
        ArgumentException.ThrowIfNullOrWhiteSpace(toCurrency);

        var from = fromCurrency.ToUpperInvariant();
        var to = toCurrency.ToUpperInvariant();

        // Identity pair — never round-trip the DB. Use UTC midnight for
        // the synthetic effective date; the caller only reads .Rate and
        // .EffectiveDate.
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return new FxRate
            {
                FromCurrency = from,
                ToCurrency = to,
                Rate = 1m,
                EffectiveDate = effectiveDate.Date,
                PublishedAt = DateTimeOffset.UtcNow,
                PublishedByUserId = Guid.Empty,
                TenantId = null
            };
        }

        // Strip the time component; rates are date-keyed.
        var date = effectiveDate.Date;

        return await _db.FxRates.AsNoTracking()
            .Where(r => r.FromCurrency == from
                        && r.ToCurrency == to
                        && r.EffectiveDate <= date)
            .OrderByDescending(r => r.EffectiveDate)
            .FirstOrDefaultAsync(ct);
    }
}
