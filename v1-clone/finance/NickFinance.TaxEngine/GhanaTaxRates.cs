namespace NickFinance.TaxEngine;

/// <summary>
/// One snapshot of Ghana indirect-tax rates effective on a given date. Held
/// as a record so we can keep historical rate sets and pick the right one
/// for any voucher / invoice based on its <c>effective_date</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ghana's VAT regime stacks the three "levies" (NHIL, GETFund, COVID-19
/// Health Recovery Levy) <em>before</em> standard VAT — the levies apply
/// to the pre-VAT base, and VAT is then charged on (base + levies). This
/// is materially different from a single 21% rate on the base. See
/// <c>FINANCE_KERNEL.md §</c> and <c>NICKFINANCE_PLATFORM.md §3</c>.
/// </para>
/// <para>
/// Rates are stored as <see cref="decimal"/>. <c>0.025m</c> = 2.5%.
/// </para>
/// </remarks>
public sealed record GhanaTaxRates(
    DateOnly EffectiveFrom,
    decimal VatRate,        // standard VAT, e.g. 0.15 (15%)
    decimal NhilRate,       // National Health Insurance Levy, 0.025 (2.5%)
    decimal GetFundRate,    // Ghana Education Trust Fund Levy, 0.025 (2.5%)
    decimal CovidRate)      // COVID-19 Health Recovery Levy, 0.01 (1%)
{
    /// <summary>
    /// Rates effective from <c>2022-05-01</c> when the COVID levy took
    /// effect at 1%. This is the v1 default — modules should explicitly
    /// pick a rate set by date once a future budget changes them.
    /// </summary>
    public static readonly GhanaTaxRates Default = new(
        EffectiveFrom: new DateOnly(2022, 5, 1),
        VatRate: 0.15m,
        NhilRate: 0.025m,
        GetFundRate: 0.025m,
        CovidRate: 0.01m);

    /// <summary>
    /// All known historical rate sets, ordered chronologically. As budget
    /// changes happen, prepend / append new rows here. Reload via config
    /// (Phase 6.5) once the production rate-table service ships.
    /// </summary>
    public static IReadOnlyList<GhanaTaxRates> History { get; } = new[]
    {
        Default
    };

    /// <summary>The rate set in force on the given date. Throws if no row covers it.</summary>
    public static GhanaTaxRates ForDate(DateOnly d)
    {
        for (var i = History.Count - 1; i >= 0; i--)
        {
            if (History[i].EffectiveFrom <= d) return History[i];
        }
        throw new InvalidOperationException(
            $"No Ghana tax rates configured for {d:yyyy-MM-dd}; the earliest known set starts {History[0].EffectiveFrom:yyyy-MM-dd}.");
    }

    /// <summary>The combined levy rate (NHIL + GETFund + COVID) — 6% under <see cref="Default"/>.</summary>
    public decimal CombinedLeviesRate => NhilRate + GetFundRate + CovidRate;

    /// <summary>
    /// The effective compound rate when applied to a NET amount:
    /// <c>(1 + levies)(1 + vat) - 1</c>. Under <see cref="Default"/> this
    /// is 21.9% (vs naive sum of 21%).
    /// </summary>
    public decimal EffectiveCompoundRate => (1m + CombinedLeviesRate) * (1m + VatRate) - 1m;
}
