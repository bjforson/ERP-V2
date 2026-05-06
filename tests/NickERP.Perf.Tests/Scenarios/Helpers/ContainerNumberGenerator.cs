using System.Text;

namespace NickERP.Perf.Tests.Scenarios.Helpers;

/// <summary>
/// Sprint 55 — generates valid ISO 6346 container numbers for perf
/// scenarios. Format is <c>OOOEUSSSSSSC</c>:
/// <list type="bullet">
///   <item><term>OOO</term><description>3-letter owner code (uppercase A-Z).</description></item>
///   <item><term>E</term><description>Equipment category (U, J, or Z; we use 'U' for freight).</description></item>
///   <item><term>SSSSSS</term><description>6-digit serial number.</description></item>
///   <item><term>C</term><description>ISO 6346 check digit computed from the first 10 chars.</description></item>
/// </list>
/// <para>
/// <b>Why ISO 6346 + valid checksum:</b> the perf test payload should
/// shape-match what real edge nodes send so any future container-number
/// validator on the case-create endpoint doesn't reject our test
/// traffic. Container numbers are the canonical pilot subject identifier
/// (per <c>InspectionCase.SubjectIdentifier</c>) and the format is
/// universal across customs jurisdictions — no Ghana-specific anchoring.
/// </para>
/// <para>
/// <b>Determinism:</b> takes a <see cref="Random"/> for repeatable runs
/// when seeded; otherwise generates fresh per call.
/// </para>
/// </summary>
public static class ContainerNumberGenerator
{
    /// <summary>
    /// ISO 6346 character → integer-equivalent table for the check digit
    /// algorithm. Letters skip multiples of 11 (so 'B' = 12, not 11).
    /// </summary>
    private static readonly Dictionary<char, int> CharValues = BuildCharValueTable();

    /// <summary>Powers of two used in the ISO 6346 weighted sum.</summary>
    private static readonly int[] Weights = { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512 };

    /// <summary>
    /// Generate one valid container number. <paramref name="rng"/> is
    /// used for both the owner-code letters and the serial digits;
    /// passing a deterministic <see cref="Random"/> yields reproducible
    /// numbers.
    /// </summary>
    public static string Generate(Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);

        var sb = new StringBuilder(11);
        // Owner code (3 letters). Leading char is conventionally a
        // shipping line prefix. Random A-Z keeps the test payload
        // vendor-neutral.
        for (var i = 0; i < 3; i++) sb.Append((char)('A' + rng.Next(26)));
        // Equipment category — 'U' is "freight container" (most common).
        sb.Append('U');
        // 6-digit serial (zero-padded).
        var serial = rng.Next(0, 1_000_000);
        sb.Append(serial.ToString("D6"));
        // Check digit on the first 10 chars.
        var checkDigit = ComputeCheckDigit(sb.ToString());
        sb.Append(checkDigit);
        return sb.ToString();
    }

    /// <summary>
    /// ISO 6346 check digit algorithm. <paramref name="prefix"/> must be
    /// exactly the first 10 characters (3-letter owner + 1-letter
    /// category + 6-digit serial). Returns 0-9.
    /// </summary>
    /// <remarks>
    /// Per ISO 6346: each char's table value is multiplied by 2^position
    /// (position 0..9), summed, mod 11. Result of 10 is conventionally
    /// reported as 0 (the "lossy" case the standard accepts).
    /// </remarks>
    public static int ComputeCheckDigit(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (prefix.Length < 10)
            throw new ArgumentException("ISO 6346 prefix must be at least 10 chars.", nameof(prefix));

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var c = prefix[i];
            if (!CharValues.TryGetValue(c, out var v))
                throw new ArgumentException($"Char '{c}' at position {i} is not valid in ISO 6346.", nameof(prefix));
            sum += v * Weights[i];
        }
        var mod = sum % 11;
        // Per ISO 6346, the check digit is the remainder mod 11; if it
        // happens to be 10, it's reported as 0 (standard caveat).
        return mod == 10 ? 0 : mod;
    }

    /// <summary>
    /// Verify a candidate container number's check digit is correct.
    /// Returns true for the canonical 11-char format only.
    /// </summary>
    public static bool IsValid(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length != 11) return false;
        var prefix = candidate[..10];
        var lastChar = candidate[10];
        if (lastChar < '0' || lastChar > '9') return false;
        try
        {
            var expected = ComputeCheckDigit(prefix);
            return expected == (lastChar - '0');
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Dictionary<char, int> BuildCharValueTable()
    {
        var table = new Dictionary<char, int>(36);
        for (var d = 0; d <= 9; d++) table[(char)('0' + d)] = d;

        // ISO 6346 letter values: A=10, B=12, C=13, ... skipping 11, 22, 33.
        var skip = new HashSet<int> { 11, 22, 33 };
        var v = 9;
        for (var c = 'A'; c <= 'Z'; c++)
        {
            do { v++; } while (skip.Contains(v));
            table[c] = v;
        }
        return table;
    }
}
