using NickERP.NickFinance.Core.Entities;

namespace NickERP.NickFinance.Core.Services;

/// <summary>
/// FX rate lookup — see G2 §1.10. Resolves the rate effective at a
/// given date for converting from one currency to another. Backed by
/// the <c>fx_rate</c> table; the implementation lives in the
/// NickFinance.Database project.
///
/// <para>
/// Resolution rule: latest row whose <see cref="FxRate.EffectiveDate"/>
/// is on-or-before the requested date. A missing rate means publishing
/// has fallen behind — the caller MUST treat that as a hard failure
/// ("FX rate not yet published for {date}; ask finance to publish")
/// rather than silently substituting a stale rate or 1.0.
/// </para>
/// </summary>
public interface IFxRateLookup
{
    /// <summary>
    /// Resolve the FX rate from <paramref name="fromCurrency"/> to
    /// <paramref name="toCurrency"/> effective at
    /// <paramref name="effectiveDate"/>. If the two currencies are equal,
    /// returns a synthetic <c>1.0</c> row without hitting the DB.
    /// </summary>
    /// <returns>
    /// The matching <see cref="FxRate"/> row, or <c>null</c> if no rate
    /// has been published for that pair on-or-before that date. Callers
    /// should fail-fast on null with a clear user-facing error.
    /// </returns>
    Task<FxRate?> ResolveAsync(
        string fromCurrency,
        string toCurrency,
        DateTime effectiveDate,
        CancellationToken ct = default);
}
