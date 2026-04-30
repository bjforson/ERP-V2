using NickFinance.Ledger;

namespace NickFinance.TaxEngine;

/// <summary>
/// Pure-function Ghana indirect-tax calculator. No DB, no DI — modules
/// pass in an amount and rates, get back a <see cref="TaxComputation"/>.
/// Used by Petty Cash on disbursement, by AR on invoicing, by AP on bill
/// capture.
/// </summary>
public static class TaxCalculator
{
    /// <summary>
    /// Compute taxes from a <em>net</em> amount (the supplier's actual
    /// income) — typical for AR invoicing where you start from the
    /// price the customer pays for the service excluding all taxes.
    /// </summary>
    public static TaxComputation FromNet(Money net, GhanaTaxRates rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (net.IsNegative)
        {
            throw new ArgumentException("Net amount cannot be negative.", nameof(net));
        }

        var nhil = net.MultiplyRate(rates.NhilRate);
        var getfund = net.MultiplyRate(rates.GetFundRate);
        var covid = net.MultiplyRate(rates.CovidRate);

        // VAT base = net + all three levies. VAT is on the levy-inclusive base.
        var vatBase = net.Add(nhil).Add(getfund).Add(covid);
        var vat = vatBase.MultiplyRate(rates.VatRate);

        var gross = net.Add(nhil).Add(getfund).Add(covid).Add(vat);

        return new TaxComputation(net, nhil, getfund, covid, vat, gross, rates);
    }

    /// <summary>
    /// Compute taxes from a <em>gross</em> amount (the all-in figure on a
    /// receipt) — typical for Petty Cash where the requester only knows
    /// what they paid at the till. We back-solve for net, then compute
    /// each component, then redistribute any rounding residual onto the
    /// largest piece (Net) so the breakdown still sums exactly to gross.
    /// </summary>
    public static TaxComputation FromGross(Money gross, GhanaTaxRates rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (gross.IsNegative)
        {
            throw new ArgumentException("Gross amount cannot be negative.", nameof(gross));
        }
        if (gross.IsZero)
        {
            return new TaxComputation(
                Money.Zero(gross.CurrencyCode),
                Money.Zero(gross.CurrencyCode),
                Money.Zero(gross.CurrencyCode),
                Money.Zero(gross.CurrencyCode),
                Money.Zero(gross.CurrencyCode),
                gross, rates);
        }

        // gross = net * (1 + levies) * (1 + vat)
        // => net = gross / ((1 + levies)(1 + vat))
        var divisor = (1m + rates.CombinedLeviesRate) * (1m + rates.VatRate);
        if (divisor == 0m)
        {
            throw new InvalidOperationException(
                "Tax-rate divisor evaluated to zero; check the rate set.");
        }
        var netDecimal = gross.Minor / divisor;
        var netMinor = (long)Math.Round(netDecimal, 0, MidpointRounding.ToEven);
        var net = new Money(netMinor, gross.CurrencyCode);

        // Build the rest from net using the same forward formula. Then
        // residual-correct so the parts add up to the gross we started with.
        var built = FromNet(net, rates);
        var residual = gross.Subtract(built.Gross).Minor;
        if (residual == 0)
        {
            return built;
        }

        // Push the residual into Net (it's the largest component, so the
        // proportional error introduced is smallest).
        var correctedNet = new Money(net.Minor + residual, gross.CurrencyCode);
        var corrected = FromNet(correctedNet, rates);

        // After correction the new gross might be off by 1 minor unit
        // depending on the parity of the residual; if so, push the
        // remaining 1 minor onto VAT (the next-largest component) so
        // the two-decimal invariant holds without changing Net further.
        if (corrected.Gross.Minor != gross.Minor)
        {
            var diff = gross.Minor - corrected.Gross.Minor;
            corrected = corrected with { Vat = new Money(corrected.Vat.Minor + diff, gross.CurrencyCode) };
            corrected = corrected with { Gross = corrected.Net.Add(corrected.Nhil).Add(corrected.GetFund).Add(corrected.Covid).Add(corrected.Vat) };
        }
        return corrected;
    }
}
