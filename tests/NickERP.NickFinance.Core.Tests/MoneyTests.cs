using NickERP.NickFinance.Core.Entities;

namespace NickERP.NickFinance.Core.Tests;

/// <summary>
/// G2 §1.1 / §1.3 — Money value object invariants. Negatives throw at
/// the type level; cross-currency arithmetic throws at the operator
/// level. These are foundational; everything in the ledger / workflow
/// trusts them.
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void Constructor_rejects_negative_amount()
    {
        var act = () => new Money(-1m, "GHS");
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*non-negative*");
    }

    [Fact]
    public void Constructor_accepts_zero()
    {
        var m = new Money(0m, "GHS");
        m.Amount.Should().Be(0m);
    }

    [Fact]
    public void Constructor_uppercases_currency_code()
    {
        var m = new Money(100m, "ghs");
        m.CurrencyCode.Should().Be("GHS");
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB")]
    [InlineData("ABCD")]
    [InlineData("  ")]
    public void Constructor_rejects_invalid_currency_code(string code)
    {
        var act = () => new Money(1m, code);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Add_same_currency_sums_amounts()
    {
        var a = new Money(10m, "GHS");
        var b = new Money(5m, "GHS");
        (a + b).Should().Be(new Money(15m, "GHS"));
    }

    [Fact]
    public void Add_different_currencies_throws()
    {
        var a = new Money(10m, "GHS");
        var b = new Money(5m, "USD");
        var act = () => { var _ = a + b; };
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GHS vs USD*");
    }

    [Fact]
    public void Subtract_same_currency_diff()
    {
        var a = new Money(10m, "GHS");
        var b = new Money(3m, "GHS");
        (a - b).Should().Be(new Money(7m, "GHS"));
    }

    [Fact]
    public void Subtract_underflow_throws()
    {
        var a = new Money(3m, "GHS");
        var b = new Money(10m, "GHS");
        var act = () => { var _ = a - b; };
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*underflowed*");
    }

    [Fact]
    public void Subtract_different_currencies_throws()
    {
        var a = new Money(10m, "GHS");
        var b = new Money(5m, "USD");
        var act = () => { var _ = a - b; };
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Multiply_by_positive_scalar_scales_amount()
    {
        var a = new Money(10m, "GHS");
        (a * 2.5m).Should().Be(new Money(25m, "GHS"));
        (2.5m * a).Should().Be(new Money(25m, "GHS"));
    }

    [Fact]
    public void Multiply_by_negative_scalar_throws()
    {
        var a = new Money(10m, "GHS");
        var act = () => { var _ = a * -2m; };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Comparison_same_currency_works()
    {
        var a = new Money(10m, "GHS");
        var b = new Money(5m, "GHS");
        (a > b).Should().BeTrue();
        (b < a).Should().BeTrue();
        var aCopy = new Money(10m, "GHS");
        (a >= aCopy).Should().BeTrue();
        (b <= a).Should().BeTrue();
    }

    [Fact]
    public void Comparison_different_currencies_throws()
    {
        var a = new Money(10m, "GHS");
        var b = new Money(5m, "USD");
        var act = () => { var _ = a > b; };
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ToString_uses_invariant_culture_and_4_decimals()
    {
        var m = new Money(1234.5m, "GHS");
        m.ToString().Should().Be("1234.5000 GHS");
    }

    [Fact]
    public void Zero_factory_returns_zero_in_currency()
    {
        Money.Zero("USD").Should().Be(new Money(0m, "USD"));
    }

    [Fact]
    public void Records_with_same_amount_and_currency_are_equal()
    {
        new Money(7m, "GHS").Should().Be(new Money(7m, "GHS"));
    }

    [Fact]
    public void Records_with_different_currencies_are_not_equal()
    {
        new Money(7m, "GHS").Should().NotBe(new Money(7m, "USD"));
    }
}
