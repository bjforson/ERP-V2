using NickFinance.Ledger;

namespace NickFinance.TaxEngine;

/// <summary>
/// The full breakdown of a tax computation. All amounts are in the same
/// currency as the input. Sums are guaranteed by construction:
/// <c>Net + Nhil + GetFund + Covid + Vat = Gross</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ghana compound order</b>:
/// <list type="number">
///   <item><description>Net — the pre-tax amount (the supplier's actual income).</description></item>
///   <item><description>Levies = Net × (NHIL + GETFund + COVID). Each is split out separately so the journal can credit the correct payable account.</description></item>
///   <item><description>VAT = (Net + Levies) × VatRate. VAT is charged on the levy-inclusive base, not the bare net.</description></item>
/// </list>
/// </para>
/// <para>
/// Rounding uses banker's rounding (<see cref="MidpointRounding.ToEven"/>)
/// at every step so re-summing the breakdown matches the Gross to the
/// last pesewa. Residuals never exceed 1 minor unit per line.
/// </para>
/// </remarks>
public sealed record TaxComputation(
    Money Net,
    Money Nhil,
    Money GetFund,
    Money Covid,
    Money Vat,
    Money Gross,
    GhanaTaxRates RatesUsed)
{
    /// <summary>Sum of the three levies as a single value.</summary>
    public Money TotalLevies => Nhil.Add(GetFund).Add(Covid);

    /// <summary>The full indirect-tax burden (levies + VAT) as a single value.</summary>
    public Money TotalIndirectTax => TotalLevies.Add(Vat);
}
