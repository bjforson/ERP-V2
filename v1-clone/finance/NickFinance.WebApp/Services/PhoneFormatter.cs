namespace NickFinance.WebApp.Services;

/// <summary>
/// E.164 → human-readable Ghana phone display ("+233 24 555 1234").
/// We don't depend on libphonenumber for v1; Ghana is the only country
/// in scope and the format is dead-simple. If a non-Ghana number ever
/// appears (international vendor, expat employee), we fall back to the
/// raw E.164 string with the leading "+" preserved.
/// </summary>
public static class PhoneFormatter
{
    /// <summary>
    /// Format an E.164 string for display. Returns the input unchanged
    /// if it isn't recognisably E.164 — lets the UI display whatever
    /// raw value the operator typed in without crashing.
    /// </summary>
    public static string Format(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164)) return string.Empty;
        var s = e164.Trim();

        // Must start with "+" and have only digits otherwise.
        if (s.Length < 8 || s[0] != '+') return s;
        for (var i = 1; i < s.Length; i++)
        {
            if (!char.IsDigit(s[i])) return s;
        }

        // Ghana — "+233" prefix. National number after the country code is
        // 9 digits; we render as "+233 NN NNN NNNN" to mirror local cards.
        if (s.StartsWith("+233") && s.Length == 13)
        {
            return $"+233 {s.Substring(4, 2)} {s.Substring(6, 3)} {s.Substring(9, 4)}";
        }

        // Fall back to whatever the caller gave us. Better than crashing.
        return s;
    }

    /// <summary>
    /// Best-effort normalisation of an operator-typed Ghana phone (e.g.
    /// "024 555 1234" or "0245551234") into E.164 ("+233245551234"). Used
    /// when we want to round-trip a typeahead match against
    /// <c>identity.user_phones</c>.
    /// </summary>
    public static string? TryToE164(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // Strip everything except digits and the leading +.
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var c in input.Trim())
        {
            if (c == '+' && sb.Length == 0) sb.Append('+');
            else if (char.IsDigit(c)) sb.Append(c);
        }
        var s = sb.ToString();
        if (s.Length == 0) return null;

        // Already E.164.
        if (s[0] == '+' && s.Length >= 8) return s;

        // Local Ghana with leading 0 → "+233" + drop the 0.
        if (s.StartsWith('0') && s.Length == 10)
        {
            return "+233" + s[1..];
        }

        // Bare 9-digit Ghana number, no leading 0.
        if (s.Length == 9 && s[0] != '0')
        {
            return "+233" + s;
        }

        // Otherwise return null — caller should treat this as "couldn't
        // normalise" and probably surface an error to the user.
        return null;
    }
}
