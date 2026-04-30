using NickFinance.Ledger;
using NickFinance.TaxEngine;
using Xunit;

namespace NickFinance.TaxEngine.Tests;

public class TaxCalculatorTests
{
    private static Money Ghs(long minor) => new(minor, "GHS");

    // -----------------------------------------------------------------
    // Default rates — sanity
    // -----------------------------------------------------------------

    [Fact]
    public void Default_Rates_AreThe2022Stack()
    {
        var r = GhanaTaxRates.Default;
        Assert.Equal(0.15m, r.VatRate);
        Assert.Equal(0.025m, r.NhilRate);
        Assert.Equal(0.025m, r.GetFundRate);
        Assert.Equal(0.01m, r.CovidRate);
        Assert.Equal(0.06m, r.CombinedLeviesRate);
    }

    [Fact]
    public void EffectiveCompoundRate_IsCorrect()
    {
        // (1.06)(1.15) - 1 = 1.219 - 1 = 0.219 = 21.9%
        Assert.Equal(0.219m, GhanaTaxRates.Default.EffectiveCompoundRate);
    }

    [Fact]
    public void ForDate_ReturnsDefault_WhenAfterEffectiveFrom()
    {
        var r = GhanaTaxRates.ForDate(new DateOnly(2026, 4, 1));
        Assert.Equal(GhanaTaxRates.Default, r);
    }

    [Fact]
    public void ForDate_Throws_WhenBeforeAnyKnownSet()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GhanaTaxRates.ForDate(new DateOnly(2000, 1, 1)));
    }

    // -----------------------------------------------------------------
    // FromNet — forward calc
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(100_00, 2_50, 2_50, 1_00, 15_90, 121_90)]   // GHS 100 → 21.90 tax
    [InlineData(1_000_00, 25_00, 25_00, 10_00, 159_00, 1_219_00)] // GHS 1,000 → 219.00 tax
    public void FromNet_ProducesCorrectBreakdown(
        long netMinor,
        long expectedNhil,
        long expectedGetFund,
        long expectedCovid,
        long expectedVat,
        long expectedGross)
    {
        var t = TaxCalculator.FromNet(Ghs(netMinor), GhanaTaxRates.Default);
        Assert.Equal(netMinor, t.Net.Minor);
        Assert.Equal(expectedNhil, t.Nhil.Minor);
        Assert.Equal(expectedGetFund, t.GetFund.Minor);
        Assert.Equal(expectedCovid, t.Covid.Minor);
        Assert.Equal(expectedVat, t.Vat.Minor);
        Assert.Equal(expectedGross, t.Gross.Minor);
    }

    [Fact]
    public void FromNet_BreakdownAlwaysSumsToGross()
    {
        // Random spot check — the +invariant is what matters.
        var rng = new Random(20260426);
        for (int i = 0; i < 200; i++)
        {
            var net = Ghs(rng.Next(1, 10_000_000));
            var t = TaxCalculator.FromNet(net, GhanaTaxRates.Default);
            var sum = t.Net.Minor + t.Nhil.Minor + t.GetFund.Minor + t.Covid.Minor + t.Vat.Minor;
            Assert.Equal(t.Gross.Minor, sum);
        }
    }

    [Fact]
    public void FromNet_Zero_ReturnsAllZero()
    {
        var t = TaxCalculator.FromNet(Ghs(0), GhanaTaxRates.Default);
        Assert.Equal(0, t.Net.Minor);
        Assert.Equal(0, t.Nhil.Minor);
        Assert.Equal(0, t.GetFund.Minor);
        Assert.Equal(0, t.Covid.Minor);
        Assert.Equal(0, t.Vat.Minor);
        Assert.Equal(0, t.Gross.Minor);
    }

    [Fact]
    public void FromNet_Negative_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            TaxCalculator.FromNet(Ghs(-100), GhanaTaxRates.Default));
    }

    // -----------------------------------------------------------------
    // FromGross — back-solve
    // -----------------------------------------------------------------

    [Fact]
    public void FromGross_RoundTripsCleanly_OnTypicalAmounts()
    {
        // For "round" net amounts, FromGross(FromNet(net)) returns net.
        long[] nets = { 100_00, 250_00, 500_00, 1_000_00, 12_345_00 };
        foreach (var n in nets)
        {
            var fwd = TaxCalculator.FromNet(Ghs(n), GhanaTaxRates.Default);
            var back = TaxCalculator.FromGross(fwd.Gross, GhanaTaxRates.Default);
            Assert.Equal(n, back.Net.Minor);
            Assert.Equal(fwd.Gross.Minor, back.Gross.Minor);
        }
    }

    [Fact]
    public void FromGross_Always_SumsToGrossExactly()
    {
        // For arbitrary gross amounts (where the perfect net might not
        // be a whole pesewa), the breakdown still sums to the input gross.
        var rng = new Random(20260426);
        for (int i = 0; i < 500; i++)
        {
            var gross = Ghs(rng.Next(1, 50_000_000));
            var t = TaxCalculator.FromGross(gross, GhanaTaxRates.Default);
            var sum = t.Net.Minor + t.Nhil.Minor + t.GetFund.Minor + t.Covid.Minor + t.Vat.Minor;
            Assert.Equal(gross.Minor, sum);
        }
    }

    [Fact]
    public void FromGross_Zero_AllZero()
    {
        var t = TaxCalculator.FromGross(Ghs(0), GhanaTaxRates.Default);
        Assert.True(t.Net.IsZero);
        Assert.True(t.Nhil.IsZero);
        Assert.True(t.Vat.IsZero);
        Assert.True(t.Gross.IsZero);
    }

    [Fact]
    public void FromGross_Negative_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            TaxCalculator.FromGross(Ghs(-100), GhanaTaxRates.Default));
    }

    // -----------------------------------------------------------------
    // TaxComputation aggregates
    // -----------------------------------------------------------------

    [Fact]
    public void TotalLevies_AndTotalIndirectTax_AreCorrect()
    {
        var t = TaxCalculator.FromNet(Ghs(1_000_00), GhanaTaxRates.Default);
        Assert.Equal(60_00, t.TotalLevies.Minor);                // 6% of 1000 = 60
        Assert.Equal(60_00 + 159_00, t.TotalIndirectTax.Minor);  // levies + 15.9% VAT
    }
}
