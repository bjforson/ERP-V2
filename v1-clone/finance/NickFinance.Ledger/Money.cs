using System.Globalization;

namespace NickFinance.Ledger;

/// <summary>
/// Value type for currency amounts. Stored as a signed count of minor units
/// (pesewa for GHS, cents for USD, etc.) alongside an ISO-4217 currency code.
/// Never uses floating-point. Never allows arithmetic between different
/// currencies — that's a bug we want to catch at compile time.
/// </summary>
public readonly record struct Money
{
    /// <summary>The amount in minor units (e.g. pesewa for GHS). Can be negative.</summary>
    public long Minor { get; }

    /// <summary>ISO-4217 three-letter code, upper case (e.g. "GHS", "USD").</summary>
    public string CurrencyCode { get; }

    public Money(long minor, string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("CurrencyCode must be a 3-letter ISO-4217 code.", nameof(currencyCode));
        Minor = minor;
        CurrencyCode = currencyCode.ToUpperInvariant();
    }

    public static Money Zero(string currencyCode) => new(0, currencyCode);
    public static Money FromMinor(long minor, string ccy) => new(minor, ccy);

    /// <summary>Construct from a major-unit decimal using banker's rounding to 2 dp.</summary>
    public static Money FromMajor(decimal major, string ccy)
    {
        // Banker's rounding is the default for MidpointRounding.ToEven on decimal.
        var rounded = Math.Round(major * 100m, 0, MidpointRounding.ToEven);
        return new Money((long)rounded, ccy);
    }

    public decimal ToMajor() => Minor / 100m;

    public Money Add(Money other)
    {
        AssertSameCurrency(other);
        return new Money(checked(Minor + other.Minor), CurrencyCode);
    }

    public Money Subtract(Money other)
    {
        AssertSameCurrency(other);
        return new Money(checked(Minor - other.Minor), CurrencyCode);
    }

    public Money Negate() => new(checked(-Minor), CurrencyCode);

    /// <summary>Multiply by a rate (e.g. 0.15 for VAT). Uses banker's rounding.</summary>
    public Money MultiplyRate(decimal rate)
    {
        var result = Math.Round(Minor * rate, 0, MidpointRounding.ToEven);
        return new Money((long)result, CurrencyCode);
    }

    public bool IsZero => Minor == 0;
    public bool IsPositive => Minor > 0;
    public bool IsNegative => Minor < 0;

    public static Money operator +(Money a, Money b) => a.Add(b);
    public static Money operator -(Money a, Money b) => a.Subtract(b);
    public static Money operator -(Money a) => a.Negate();

    public override string ToString()
        => $"{CurrencyCode} {ToMajor().ToString("N2", CultureInfo.InvariantCulture)}";

    private void AssertSameCurrency(Money other)
    {
        if (CurrencyCode != other.CurrencyCode)
            throw new InvalidOperationException(
                $"Cannot mix currencies in an operation: {CurrencyCode} vs {other.CurrencyCode}.");
    }
}
