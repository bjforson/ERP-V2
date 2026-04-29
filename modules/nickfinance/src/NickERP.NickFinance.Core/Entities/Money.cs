using System.Globalization;

namespace NickERP.NickFinance.Core.Entities;

/// <summary>
/// Currency-aware money value object — see G2 §1.1 / §1.3 / §1.6 / §1.8.
///
/// <para>
/// The amount is always non-negative; direction (cash leaving / entering /
/// adjustment) is carried separately on
/// <see cref="Enums.LedgerDirection"/>. This is a deliberate choice:
/// negative amounts in domain code are an enormous source of sign-error
/// bugs ("did the customer pay us or did we pay them?"). By forbidding
/// the sign and making direction explicit, every reader of a Money
/// instance knows what they're looking at without context.
/// </para>
///
/// <para>
/// Currency is an ISO 4217 code (<c>"GHS"</c>, <c>"USD"</c>, <c>"EUR"</c>);
/// arithmetic operators reject mismatched currencies at the type level so
/// the wrong-currency-add bug becomes a compile-time / runtime error
/// instead of producing a subtly-wrong total. Cross-currency arithmetic
/// must go through the FX layer (<c>IFxRateLookup</c>) which yields a
/// <see cref="Money"/> in a target currency.
/// </para>
///
/// <para>
/// Persistence shape: <c>numeric(18,4)</c> for amount, <c>text</c> for
/// currency code. EF value-converter splits the record into its two
/// columns. JSON serialisation goes via the implicit
/// <see cref="ToString"/> form (<c>"123.4500 GHS"</c>) for audit-event
/// payloads where readability matters more than parser ergonomics.
/// </para>
/// </summary>
public readonly record struct Money
{
    /// <summary>Backing field for <see cref="Amount"/>.</summary>
    private readonly decimal _amount;

    /// <summary>Backing field for <see cref="CurrencyCode"/>.</summary>
    private readonly string _currencyCode;

    /// <summary>
    /// Construct a Money value. Throws on negative amounts; throws on
    /// missing or non-3-letter currency codes.
    /// </summary>
    public Money(decimal amount, string currencyCode)
    {
        if (amount < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                "Money.Amount must be non-negative; carry direction on the ledger event, not the amount.");
        }

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
        {
            throw new ArgumentException(
                "CurrencyCode must be a non-empty 3-letter ISO 4217 code (e.g. 'GHS').",
                nameof(currencyCode));
        }

        _amount = amount;
        _currencyCode = currencyCode.ToUpperInvariant();
    }

    /// <summary>The non-negative amount (<c>decimal(18,4)</c> in storage).</summary>
    public decimal Amount => _amount;

    /// <summary>The ISO 4217 currency code, always uppercase.</summary>
    public string CurrencyCode => _currencyCode ?? string.Empty;

    /// <summary>
    /// A zero-valued Money in the given currency. Useful as the seed
    /// for opening balances and aggregations.
    /// </summary>
    public static Money Zero(string currencyCode) => new(0m, currencyCode);

    /// <summary>
    /// Add two Money values. Throws if the currencies don't match —
    /// cross-currency adds must go through the FX layer.
    /// </summary>
    public static Money operator +(Money left, Money right)
    {
        AssertSameCurrency(left, right, "+");
        return new Money(left._amount + right._amount, left._currencyCode);
    }

    /// <summary>
    /// Subtract two Money values. Throws if the currencies don't match,
    /// or if the result would be negative (Money is non-negative — use
    /// a <see cref="Enums.LedgerDirection"/> on the event instead).
    /// </summary>
    public static Money operator -(Money left, Money right)
    {
        AssertSameCurrency(left, right, "-");
        var result = left._amount - right._amount;
        if (result < 0m)
        {
            throw new InvalidOperationException(
                $"Money subtraction underflowed ({left} - {right} = {result} {left._currencyCode}). "
                + "Money is non-negative; if you need to express a negative balance, "
                + "carry direction on the ledger event.");
        }
        return new Money(result, left._currencyCode);
    }

    /// <summary>
    /// Multiply a Money value by a non-negative scalar. Used by FX
    /// conversion (rate * amount). Throws on negative scalar.
    /// </summary>
    public static Money operator *(Money left, decimal scalar)
    {
        if (scalar < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scalar),
                scalar,
                "Money * scalar requires a non-negative scalar; flip direction on the event instead.");
        }
        return new Money(left._amount * scalar, left._currencyCode);
    }

    /// <summary>Symmetric form so <c>scalar * money</c> works too.</summary>
    public static Money operator *(decimal scalar, Money right) => right * scalar;

    /// <summary>
    /// Compare two Money values. Throws on mismatched currency — there's
    /// no sensible answer to "is 100 USD greater than 100 GHS"; force the
    /// caller to convert first.
    /// </summary>
    public static bool operator >(Money left, Money right)
    {
        AssertSameCurrency(left, right, ">");
        return left._amount > right._amount;
    }

    /// <inheritdoc cref="op_GreaterThan(Money, Money)"/>
    public static bool operator <(Money left, Money right)
    {
        AssertSameCurrency(left, right, "<");
        return left._amount < right._amount;
    }

    /// <inheritdoc cref="op_GreaterThan(Money, Money)"/>
    public static bool operator >=(Money left, Money right)
    {
        AssertSameCurrency(left, right, ">=");
        return left._amount >= right._amount;
    }

    /// <inheritdoc cref="op_GreaterThan(Money, Money)"/>
    public static bool operator <=(Money left, Money right)
    {
        AssertSameCurrency(left, right, "<=");
        return left._amount <= right._amount;
    }

    /// <summary>
    /// Stable string form: <c>"123.4500 GHS"</c>. Consumed by audit-event
    /// payloads where the human-readable shape is more useful than two
    /// nested fields. Use invariant culture so the decimal point is
    /// always a period regardless of host locale.
    /// </summary>
    public override string ToString()
        => $"{_amount.ToString("F4", CultureInfo.InvariantCulture)} {_currencyCode}";

    private static void AssertSameCurrency(Money left, Money right, string op)
    {
        if (!string.Equals(left._currencyCode, right._currencyCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot apply '{op}' to Money values with different currencies "
                + $"({left._currencyCode} vs {right._currencyCode}). "
                + "Cross-currency arithmetic must go through the FX layer (IFxRateLookup).");
        }
    }
}
