using NickFinance.Ledger;
using Xunit;

namespace NickFinance.Ledger.Tests;

/// <summary>Unit tests on the Money value type — no DB needed.</summary>
public class MoneyTests
{
    [Fact]
    public void Zero_IsZero()
    {
        var z = Money.Zero("GHS");
        Assert.True(z.IsZero);
        Assert.Equal(0, z.Minor);
        Assert.Equal("GHS", z.CurrencyCode);
    }

    [Fact]
    public void Add_SameCurrency_Works()
    {
        var a = Money.FromMinor(500, "GHS");
        var b = Money.FromMinor(300, "GHS");
        var sum = a + b;
        Assert.Equal(800, sum.Minor);
        Assert.Equal("GHS", sum.CurrencyCode);
    }

    [Fact]
    public void Add_DifferentCurrency_Throws()
    {
        var ghs = Money.FromMinor(500, "GHS");
        var usd = Money.FromMinor(500, "USD");
        Assert.Throws<InvalidOperationException>(() => ghs + usd);
    }

    [Theory]
    [InlineData(0.00, 0)]
    [InlineData(0.01, 1)]
    [InlineData(1.00, 100)]
    [InlineData(5000.00, 500000)]
    [InlineData(1.995, 200)]      // banker's rounding: 199.5 → 200
    [InlineData(1.985, 198)]      // banker's rounding: 198.5 → 198
    [InlineData(-2.50, -250)]
    public void FromMajor_BankerRounds(decimal major, long expectedMinor)
    {
        var m = Money.FromMajor(major, "GHS");
        Assert.Equal(expectedMinor, m.Minor);
    }

    [Theory]
    [InlineData(100L, 0.15, 15L)]       // 15% of 100 pesewa = 15 pesewa
    [InlineData(100000L, 0.15, 15000L)] // 15% of GHS 1000 = GHS 150
    [InlineData(333L, 0.075, 25L)]      // 7.5% of 333 pesewa = 24.975 → 25 (ToEven: 24.975 → 25)
    [InlineData(334L, 0.075, 25L)]      // 7.5% of 334 = 25.05 → 25
    [InlineData(0L, 0.15, 0L)]
    public void MultiplyRate_Rounds(long minor, decimal rate, long expected)
    {
        var m = new Money(minor, "GHS").MultiplyRate(rate);
        Assert.Equal(expected, m.Minor);
    }

    [Fact]
    public void Negate_FlipsSign()
    {
        var m = Money.FromMinor(500, "GHS");
        var n = -m;
        Assert.Equal(-500, n.Minor);
        Assert.Equal("GHS", n.CurrencyCode);
    }

    [Fact]
    public void ToString_FormatsAsExpected()
    {
        var m = Money.FromMajor(1234.56m, "GHS");
        Assert.Equal("GHS 1,234.56", m.ToString());
    }

    [Fact]
    public void Constructor_RejectsBadCurrencyCode()
    {
        Assert.Throws<ArgumentException>(() => new Money(100, "GH"));
        Assert.Throws<ArgumentException>(() => new Money(100, ""));
        Assert.Throws<ArgumentException>(() => new Money(100, "GHS4"));
    }

    [Fact]
    public void Constructor_UppercasesCurrencyCode()
    {
        var m = new Money(100, "ghs");
        Assert.Equal("GHS", m.CurrencyCode);
    }
}
