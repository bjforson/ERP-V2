// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// Iso6346Gate — port of the v2 plugin's Iso6346 helper into the harness.
// Kept as a separate copy because the plugin's symbol is `internal` (not
// reachable across project boundaries) and harness must not weaken
// visibility on plugin internals just to bring up an evaluator.
// Divergence between this file and
// modules/inspection/plugins/NickERP.Inspection.Inference.OCR.ContainerNumber/Iso6346.cs
// is a real bug — both must move together if the table mapping ever
// changes.

namespace NickERP.Tools.OcrEvaluation;

internal static class Iso6346Gate
{
    /// <summary>The literal sentinel returned when the model cannot decode a plate.</summary>
    public const string UnreadableSentinel = "<unreadable>";

    /// <summary>
    /// ISO 6346 letter-to-numeric values. The standard skips any value
    /// divisible by 11 (so 11, 22, 33 are absent) which is why the table is
    /// not a simple A=10, B=11, ... mapping.
    /// </summary>
    private static readonly int[] LetterValues = new[]
    {
        // A   B   C   D   E   F   G   H   I   J   K   L   M
        10, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 23, 24,
        // N   O   P   Q   R   S   T   U   V   W   X   Y   Z
        25, 26, 27, 28, 29, 30, 31, 32, 34, 35, 36, 37, 38,
    };

    public static bool IsValid(string? candidate)
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
    /// Normalise an OCR-extracted candidate. Strips whitespace and common
    /// punctuation, upper-cases letters. Does NOT enforce the strict
    /// grammar — that is <see cref="IsValid"/>'s job. Returns null if no
    /// 11-character candidate can be reasonably produced.
    /// </summary>
    public static string? Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }
        var s = sb.ToString();
        return s.Length == 0 ? null : s;
    }
}
