using System.Globalization;

namespace NickFinance.WebApp.Services;

/// <summary>
/// Centralised money formatter — replaces the
/// <c>(minor / 100m).ToString("N2")</c> pattern that was repeated in 31
/// places across the codebase. Always emits the currency code prefix
/// (e.g. "GHS 1,234.56") so multi-currency amounts can never visually
/// collide. Default culture is en-GH; falls back to InvariantCulture
/// when the runtime image doesn't carry the en-GH locale (older Linux
/// base images miss it).
/// </summary>
public static class MoneyFormatter
{
    private static readonly CultureInfo _defaultCulture = ResolveDefault();

    private static CultureInfo ResolveDefault()
    {
        try { return CultureInfo.GetCultureInfo("en-GH"); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>
    /// Format a minor-units amount with its currency code prefix —
    /// "GHS 1,234.56", "USD 95.00", etc. <paramref name="culture"/>
    /// overrides the default; missing/invalid culture falls back to
    /// en-GH then invariant.
    /// </summary>
    public static string Format(long minor, string currency, string? culture = null)
    {
        var ci = culture is null ? _defaultCulture : SafeCulture(culture);
        var major = minor / 100m;
        var number = major.ToString("N2", ci);
        return string.IsNullOrEmpty(currency) ? number : $"{currency} {number}";
    }

    /// <summary>
    /// Format without the currency prefix. Useful inside a money-only
    /// column where the header already names the currency.
    /// </summary>
    public static string FormatBare(long minor, string? culture = null)
    {
        var ci = culture is null ? _defaultCulture : SafeCulture(culture);
        var major = minor / 100m;
        return major.ToString("N2", ci);
    }

    private static CultureInfo SafeCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return _defaultCulture; }
    }
}
