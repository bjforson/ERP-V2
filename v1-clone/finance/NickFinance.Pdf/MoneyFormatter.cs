using System.Globalization;

namespace NickFinance.Pdf;

/// <summary>
/// Tiny shared helper for formatting minor-unit amounts as
/// human-readable money strings on PDFs. Centralised so all three
/// generators format figures identically.
/// </summary>
internal static class MoneyFormatter
{
    /// <summary>
    /// Format a minor-unit amount as <c>"1,234.56 GHS"</c>. Rounds to 2dp
    /// (every supported currency in v1 is two-decimal). The invariant
    /// culture is used so PDFs generated on a server with a German locale
    /// don't suddenly use comma decimals.
    /// </summary>
    public static string Format(long minor, string currencyCode)
    {
        var amount = minor / 100m;
        return string.Create(CultureInfo.InvariantCulture, $"{amount:N2} {currencyCode}");
    }

    /// <summary>Same as <see cref="Format"/> but renders a leading minus for negative values cleanly.</summary>
    public static string FormatSigned(long minor, string currencyCode) =>
        Format(minor, currencyCode);
}
