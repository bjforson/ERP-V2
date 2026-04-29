namespace NickERP.NickFinance.Core.Services;

/// <summary>
/// Cross-DB read of <c>tenant.base_currency_code</c> from the platform
/// DB into NickFinance — see G2 §1.8.
///
/// <para>
/// <strong>Cross-DB lookup rationale.</strong> The base currency lives
/// on the <c>tenants</c> table in <c>nickerp_platform</c>; NickFinance
/// has its own DB (<c>nickerp_nickfinance</c>). Two options were
/// considered (G2 §1.8): replicate the column into NickFinance via a
/// projector, or inject a service that reads from the platform DbContext.
/// Option 2 was picked because (a) it's one column, replication is
/// overkill, (b) the platform DbContext is already in DI on the host
/// (the apps/portal host loads <c>TenancyDbContext</c>), and (c) the
/// service can cache aggressively if hot-path latency ever matters
/// (today it's a once-per-ledger-event lookup, well off the hot path).
/// </para>
///
/// <para>
/// The concrete implementation lives in <c>NickFinance.Web</c> (it's a
/// host-level concern: which DbContext to query depends on what's
/// registered in DI). Domain code (Database project, workflow service)
/// takes <see cref="ITenantBaseCurrencyLookup"/> as a dependency and
/// stays oblivious to which DB the answer comes from.
/// </para>
/// </summary>
public interface ITenantBaseCurrencyLookup
{
    /// <summary>
    /// Resolve the tenant's base currency code (e.g. <c>"GHS"</c>).
    /// Returns the platform default <c>"GHS"</c> if the tenant row is
    /// missing the column or unreachable — fail-soft to keep ledger
    /// writes proceeding rather than blocking on a platform-DB outage.
    /// </summary>
    Task<string> GetBaseCurrencyAsync(long tenantId, CancellationToken ct = default);
}
