namespace NickFinance.Ledger;

/// <summary>
/// Read-side foreign-exchange conversion. Lives in the kernel so reports
/// (Trial Balance, Site P&amp;L, Cash Flow) and — once Wave 3 lands — the
/// period-close revaluation service can both consume it without leaking
/// a Banking dependency back into the kernel.
/// </summary>
/// <remarks>
/// Implementations live in <c>NickFinance.Banking</c> (FxRateService) and
/// any test fixture that wants to stub conversion. Phase 1 (Wave 2B) is
/// read-only; Phase 2 (Wave 3) will add the revaluation service that
/// generates journals from drift between historical and current rates.
/// </remarks>
public interface IFxConverter
{
    /// <summary>
    /// Convert <paramref name="amount"/> to <paramref name="toCurrency"/> using the
    /// rate as of <paramref name="asOf"/>. If amount.Currency == toCurrency, returns amount unchanged.
    /// Falls back to the latest rate strictly before asOf if no exact-day rate exists.
    /// Throws <see cref="MissingFxRateException"/> if no rate at all is available.
    /// </summary>
    Task<Money> ConvertAsync(Money amount, string toCurrency, DateOnly asOf, long tenantId = 1, CancellationToken ct = default);

    /// <summary>Returns the raw rate, or null if unknown.</summary>
    Task<decimal?> GetRateAsync(string fromCurrency, string toCurrency, DateOnly asOf, long tenantId = 1, CancellationToken ct = default);
}

/// <summary>
/// Thrown when <see cref="IFxConverter.ConvertAsync"/> can't find any rate
/// for the requested pair on or before the as-of date. Callers (reports,
/// the future revaluation service) treat this as "ask the operator to
/// upload a rate" — not a hard failure but a workflow signal.
/// </summary>
public sealed class MissingFxRateException(string from, string to, DateOnly asOf, long tenantId)
    : Exception($"No FX rate from {from} to {to} as of {asOf:yyyy-MM-dd} for tenant {tenantId}.")
{
    public string FromCurrency { get; } = from;
    public string ToCurrency { get; } = to;
    public DateOnly AsOf { get; } = asOf;
    public long TenantId { get; } = tenantId;
}
