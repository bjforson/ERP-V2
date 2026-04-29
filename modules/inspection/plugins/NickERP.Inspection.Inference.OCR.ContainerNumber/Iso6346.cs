namespace NickERP.Inspection.Inference.OCR.ContainerNumber;

/// <summary>
/// ISO 6346 container number format helpers. Implements the mod-11 check
/// digit defined in §6 of the standard (and reproduced in BIC's owner
/// registry documentation): each of the first 10 characters is mapped to a
/// number, multiplied by <c>2^position</c>, summed, then taken modulo 11.
/// The result modulo 10 is the expected last-digit; <c>11</c> is invalid
/// and excluded from issued ranges per the standard.
/// </summary>
internal static class Iso6346
{
    /// <summary>The literal sentinel returned when the model cannot decode a plate.</summary>
    public const string UnreadableSentinel = "<unreadable>";

    /// <summary>
    /// ISO 6346 letter-to-numeric values. The standard skips any value
    /// divisible by 11 (so <c>11, 22, 33</c> are absent) which is why the
    /// table is not a simple <c>A=10, B=11, …</c> mapping.
    /// </summary>
    private static readonly int[] LetterValues = new[]
    {
        // A   B   C   D   E   F   G   H   I   J   K   L   M
        10, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23, 24,
        // N   O   P   Q   R   S   T   U   V   W   X   Y   Z
        25, 26, 27, 28, 29, 30, 31, 32, 34, 35, 36, 37, 38,
    };

    /// <summary>
    /// Returns true iff <paramref name="candidate"/> matches the strict
    /// ISO 6346 grammar: 4 uppercase letters + 6 digits + 1 digit, with a
    /// check digit consistent with the first 10 characters. Whitespace and
    /// case variants are caller responsibility — this function is strict.
    /// </summary>
    public static bool IsValid(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length != 11)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            var c = candidate[i];
            if (c < 'A' || c > 'Z') return false;
        }
        for (var i = 4; i < 11; i++)
        {
            var c = candidate[i];
            if (c < '0' || c > '9') return false;
        }

        var expected = ComputeCheckDigit(candidate.AsSpan(0, 10));
        if (expected < 0) return false;
        return candidate[10] - '0' == expected;
    }

    /// <summary>
    /// Compute the ISO 6346 check digit for the 10-character prefix.
    /// <list type="bullet">
    ///   <item>Returns <c>-1</c> when the prefix is malformed (bad characters / wrong length).</item>
    ///   <item>Returns <c>-1</c> when <c>sum mod 11 == 10</c>: per the standard
    ///     these prefixes are not issued (no single-digit check value), so
    ///     any candidate carrying them is invalid by construction.</item>
    ///   <item>Returns the check digit in <c>[0, 9]</c> on success.</item>
    /// </list>
    /// </summary>
    public static int ComputeCheckDigit(ReadOnlySpan<char> prefix10)
    {
        if (prefix10.Length != 10) return -1;

        long sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var c = prefix10[i];
            int v;
            if (i < 4)
            {
                if (c < 'A' || c > 'Z') return -1;
                v = LetterValues[c - 'A'];
            }
            else
            {
                if (c < '0' || c > '9') return -1;
                v = c - '0';
            }
            sum += (long)v << i; // 2^i weighting per the spec
        }
        var mod = (int)(sum % 11);
        // mod == 10 is reserved by the standard — never issued. Reject.
        if (mod == 10) return -1;
        return mod;
    }

    /// <summary>
    /// Allowed token alphabet per §6.1.3 "Decoder constraint". Constrained
    /// beam search must restrict the output vocabulary to this set plus the
    /// sentinel; postprocess rejects anything outside it.
    /// </summary>
    public static ReadOnlySpan<char> AllowedAlphabet => "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".AsSpan();
}
