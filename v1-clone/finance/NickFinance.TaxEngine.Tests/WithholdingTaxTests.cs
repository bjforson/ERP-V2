using NickFinance.Ledger;
using NickFinance.TaxEngine;
using Xunit;

namespace NickFinance.TaxEngine.Tests;

public class WithholdingTaxTests
{
    private static Money Ghs(long minor) => new(minor, "GHS");

    [Theory]
    [InlineData(WhtTransactionType.SupplyOfGoods,                 true,  0.03)]
    [InlineData(WhtTransactionType.SupplyOfGoods,                 false, 0.07)]
    [InlineData(WhtTransactionType.SupplyOfWorks,                 true,  0.05)]
    [InlineData(WhtTransactionType.SupplyOfServices,              true,  0.075)]
    [InlineData(WhtTransactionType.ManagementTechnicalConsulting, true,  0.075)]
    [InlineData(WhtTransactionType.Rent,                          true,  0.08)]
    [InlineData(WhtTransactionType.CommissionToAgents,            true,  0.10)]
    [InlineData(WhtTransactionType.EndorsementsRoyaltiesEtc,      true,  0.15)]
    [InlineData(WhtTransactionType.Exempt,                        true,  0.00)]
    public void RateFor_KnownTypes(WhtTransactionType t, bool vatRegistered, decimal expected)
    {
        Assert.Equal(expected, WithholdingTax.RateFor(t, vatRegistered));
    }

    [Fact]
    public void Compute_SupplyOfServices_DeductsCorrectly()
    {
        // 7.5% of GHS 1,000 = 75; net to supplier = 925
        var w = WithholdingTax.Compute(Ghs(1_000_00), WhtTransactionType.SupplyOfServices);
        Assert.Equal(0.075m, w.Rate);
        Assert.Equal(75_00, w.WhtDeducted.Minor);
        Assert.Equal(925_00, w.NetToSupplier.Minor);
    }

    [Fact]
    public void Compute_GoodsToNonVatVendor_Uses7Percent()
    {
        var w = WithholdingTax.Compute(Ghs(500_00), WhtTransactionType.SupplyOfGoods, vendorIsVatRegistered: false);
        Assert.Equal(0.07m, w.Rate);
        Assert.Equal(35_00, w.WhtDeducted.Minor);
        Assert.Equal(465_00, w.NetToSupplier.Minor);
    }

    [Fact]
    public void Compute_Exempt_DeductsZero()
    {
        var w = WithholdingTax.Compute(Ghs(1_000_00), WhtTransactionType.Exempt);
        Assert.Equal(0m, w.Rate);
        Assert.Equal(0, w.WhtDeducted.Minor);
        Assert.Equal(1_000_00, w.NetToSupplier.Minor);
    }

    [Fact]
    public void Compute_Negative_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            WithholdingTax.Compute(Ghs(-100), WhtTransactionType.SupplyOfServices));
    }
}
