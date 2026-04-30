using NickFinance.Ledger;

namespace NickFinance.TaxEngine;

/// <summary>
/// Withholding-tax classification per Income Tax Act 896 Sixth Schedule.
/// Caller passes a <see cref="WhtTransactionType"/> (and, for some types,
/// the vendor's VAT-registered status) and gets the rate to apply.
/// </summary>
/// <remarks>
/// Resident taxpayer rates only — non-resident rates have a separate
/// (higher) schedule and aren't yet wired in.
/// </remarks>
public enum WhtTransactionType
{
    /// <summary>Supply of general goods. 3% on resident VAT-registered, 7% on non-VAT-registered.</summary>
    SupplyOfGoods = 1,

    /// <summary>Supply of works (construction, fabrication). 5%.</summary>
    SupplyOfWorks = 2,

    /// <summary>Supply of services other than management/technical. 7.5%.</summary>
    SupplyOfServices = 3,

    /// <summary>Management, technical, consulting fees to residents. 7.5%.</summary>
    ManagementTechnicalConsulting = 4,

    /// <summary>Rent paid to a resident. 8%.</summary>
    Rent = 5,

    /// <summary>Commissions paid to insurance / sales agents. 10%.</summary>
    CommissionToAgents = 6,

    /// <summary>Endorsement fees, lottery winnings, royalties, natural resource payments. 15%.</summary>
    EndorsementsRoyaltiesEtc = 7,

    /// <summary>Payment exempt from WHT — vendor holds an exemption certificate, payment is below the GHS 2,000 threshold, etc.</summary>
    Exempt = 99
}

/// <summary>The result of a WHT computation — the rate, the deduction, and the net cash that actually goes to the supplier.</summary>
public sealed record WhtComputation(
    WhtTransactionType TransactionType,
    decimal Rate,
    Money GrossPaid,
    Money WhtDeducted,
    Money NetToSupplier);

/// <summary>Resolves WHT rates and computes the deduction.</summary>
public static class WithholdingTax
{
    /// <summary>
    /// Resident WHT rates per Sixth Schedule. Non-resident rates and
    /// special cases (e.g. supply of goods to a non-VAT-registered vendor)
    /// are layered on top via the <c>vendorIsVatRegistered</c> flag where
    /// it matters.
    /// </summary>
    public static decimal RateFor(WhtTransactionType type, bool vendorIsVatRegistered = true) => type switch
    {
        WhtTransactionType.SupplyOfGoods                  => vendorIsVatRegistered ? 0.03m : 0.07m,
        WhtTransactionType.SupplyOfWorks                  => 0.05m,
        WhtTransactionType.SupplyOfServices               => 0.075m,
        WhtTransactionType.ManagementTechnicalConsulting  => 0.075m,
        WhtTransactionType.Rent                           => 0.08m,
        WhtTransactionType.CommissionToAgents             => 0.10m,
        WhtTransactionType.EndorsementsRoyaltiesEtc       => 0.15m,
        WhtTransactionType.Exempt                         => 0m,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown WhtTransactionType.")
    };

    /// <summary>
    /// Compute WHT given the gross paid amount. WHT is deducted from the
    /// supplier's net — Nick pays the deduction to GRA, the supplier
    /// receives the net.
    /// </summary>
    public static WhtComputation Compute(
        Money gross,
        WhtTransactionType type,
        bool vendorIsVatRegistered = true)
    {
        if (gross.IsNegative)
        {
            throw new ArgumentException("Gross amount cannot be negative.", nameof(gross));
        }

        var rate = RateFor(type, vendorIsVatRegistered);
        var deducted = gross.MultiplyRate(rate);
        var net = gross.Subtract(deducted);
        return new WhtComputation(type, rate, gross, deducted, net);
    }
}
