using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NickERP.NickFinance.Core.Services;
using NickERP.Platform.Tenancy.Database;

namespace NickERP.NickFinance.Web.Services;

/// <summary>
/// Cross-DB read of the tenant base currency — see G2 §1.8.
///
/// <para>
/// <strong>Schema decision (departure from literal spec text).</strong>
/// G2 §1.8 calls for a new <c>tenant.base_currency_code</c> column. The
/// existing <c>tenants</c> table already has a <c>Currency</c> column
/// with default <c>'GHS'</c> that carries exactly the semantic the spec
/// asks for ("default currency for invoices, journal entries, payroll —
/// ISO 4217"). Adding a second column with the same meaning would
/// create drift; renaming the existing one is a platform-DB schema
/// change that doesn't belong in a NickFinance pathfinder migration.
/// We therefore read from <c>Tenant.Currency</c> and document the
/// decision in <c>docs/ARCHITECTURE.md</c> §13. A future sprint can
/// rename the column platform-wide if the naming bothers anyone.
/// </para>
///
/// <para>
/// <strong>Cross-DB approach.</strong> The lookup queries
/// <c>TenancyDbContext</c> (which lives in <c>nickerp_platform</c>),
/// not <c>NickFinanceDbContext</c> (<c>nickerp_nickfinance</c>). Per
/// the spec, NickFinance entity FKs do NOT cross DB boundaries — they
/// store the tenant id as a long and trust this service for any
/// platform-side metadata. Cached in-process for 5 minutes; tenant
/// base-currency changes are extremely rare and a 5-minute lag is
/// acceptable for the pathfinder.
/// </para>
/// </summary>
public sealed class TenantBaseCurrencyLookup : ITenantBaseCurrencyLookup
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "nickfinance.tenant.base_currency.";
    private const string FallbackCurrency = "GHS";

    private readonly TenancyDbContext _tenancy;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantBaseCurrencyLookup> _logger;

    public TenantBaseCurrencyLookup(
        TenancyDbContext tenancy,
        IMemoryCache cache,
        ILogger<TenantBaseCurrencyLookup> logger)
    {
        _tenancy = tenancy ?? throw new ArgumentNullException(nameof(tenancy));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> GetBaseCurrencyAsync(long tenantId, CancellationToken ct = default)
    {
        var cacheKey = CacheKeyPrefix + tenantId.ToString();
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        try
        {
            var currency = await _tenancy.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => t.Currency)
                .FirstOrDefaultAsync(ct);

            var resolved = string.IsNullOrWhiteSpace(currency) ? FallbackCurrency : currency!;
            _cache.Set(cacheKey, resolved, CacheTtl);
            return resolved;
        }
        catch (Exception ex)
        {
            // Fail-soft: a platform-DB hiccup must not block ledger writes.
            // Returning the fallback currency means cross-currency boxes in
            // a brief outage will get base-amount snapshots in GHS. That's
            // wrong but recoverable; blocking the ledger entirely is worse.
            _logger.LogWarning(ex,
                "TenantBaseCurrencyLookup failed for tenant {TenantId}; using fallback {Fallback}",
                tenantId, FallbackCurrency);
            return FallbackCurrency;
        }
    }
}
