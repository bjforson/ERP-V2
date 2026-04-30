using System.Globalization;
using System.Text;

namespace NickFinance.Banking;

/// <summary>
/// Bank-specific CSV parser. Implementations exist per bank because the
/// column layout differs across GCB / Ecobank / Stanbic / Fidelity / Cal
/// / Zenith. v1 ships <see cref="GenericBankCsvParser"/> (date,amount,ref,
/// description); per-bank parsers slot in by registering against
/// <see cref="BankCsvParserRegistry"/>.
/// </summary>
public interface IBankCsvParser
{
    string Name { get; }
    Task<ParsedStatement> ParseAsync(byte[] content, string currencyCode, CancellationToken ct = default);
}

public sealed record ParsedStatement(
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    long OpeningBalanceMinor,
    long ClosingBalanceMinor,
    IReadOnlyList<ParsedRow> Rows);

public sealed record ParsedRow(
    DateOnly TransactionDate,
    DateOnly? ValueDate,
    string Description,
    string? Reference,
    BankTransactionDirection Direction,
    long AmountMinor);

/// <summary>
/// Registry of named CSV parsers. Hosts call <see cref="Register"/> at
/// startup for any custom parsers; <see cref="Find"/> returns the parser
/// by name or <see cref="GenericBankCsvParser"/> as the safe default.
/// </summary>
public sealed class BankCsvParserRegistry
{
    private readonly Dictionary<string, IBankCsvParser> _parsers = new(StringComparer.OrdinalIgnoreCase);

    public BankCsvParserRegistry()
    {
        Register(new GenericBankCsvParser());
    }

    public void Register(IBankCsvParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parsers[parser.Name] = parser;
    }

    public IBankCsvParser Find(string name) =>
        _parsers.TryGetValue(name, out var p) ? p : _parsers["generic"];

    public IReadOnlyCollection<string> RegisteredParsers => _parsers.Keys;
}

/// <summary>
/// Tolerant CSV parser. Expects a header row with columns named
/// approximately <c>Date | Description | Reference | Debit | Credit</c>
/// (case-insensitive, fuzzy whitespace). Decimal amounts in the local
/// minor convention (GHS = pesewa; 1234.56 → 123456 minor).
/// </summary>
public sealed class GenericBankCsvParser : IBankCsvParser
{
    public string Name => "generic";

    public Task<ParsedStatement> ParseAsync(byte[] content, string currencyCode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new ArgumentException("CSV is empty.", nameof(content));

        var text = Encoding.UTF8.GetString(content);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count < 2) throw new InvalidOperationException("CSV needs a header row and at least one data row.");

        var header = SplitCsvRow(lines[0]).Select(NormaliseColumnName).ToList();
        int dateIdx = IndexOf(header, "date");
        int descIdx = IndexOf(header, "description") is var d && d >= 0 ? d : IndexOf(header, "narration");
        int refIdx = IndexOf(header, "reference");
        int debitIdx = IndexOf(header, "debit");
        int creditIdx = IndexOf(header, "credit");
        int amountIdx = IndexOf(header, "amount");

        if (dateIdx < 0) throw new InvalidOperationException("CSV must have a 'Date' column.");
        if (descIdx < 0) throw new InvalidOperationException("CSV must have a 'Description' or 'Narration' column.");
        if (debitIdx < 0 && creditIdx < 0 && amountIdx < 0)
        {
            throw new InvalidOperationException("CSV must have a 'Debit'+'Credit' pair OR an 'Amount' column.");
        }

        var rows = new List<ParsedRow>(lines.Count - 1);
        long open = 0, close = 0;
        DateOnly? minD = null, maxD = null;

        foreach (var line in lines.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            var cells = SplitCsvRow(line);
            if (cells.Count == 0) continue;

            if (!TryParseDate(cells.ElementAtOrDefault(dateIdx), out var date)) continue;
            var desc = cells.ElementAtOrDefault(descIdx) ?? string.Empty;
            var refField = refIdx >= 0 ? cells.ElementAtOrDefault(refIdx) : null;

            long amountMinor;
            BankTransactionDirection direction;
            if (debitIdx >= 0 || creditIdx >= 0)
            {
                var dr = ParseAmountMinor(cells.ElementAtOrDefault(debitIdx));
                var cr = ParseAmountMinor(cells.ElementAtOrDefault(creditIdx));
                if (dr > 0) { amountMinor = dr; direction = BankTransactionDirection.Debit; }
                else if (cr > 0) { amountMinor = cr; direction = BankTransactionDirection.Credit; }
                else continue;
            }
            else
            {
                var amt = ParseAmountMinor(cells.ElementAtOrDefault(amountIdx));
                if (amt == 0) continue;
                if (amt < 0) { amountMinor = -amt; direction = BankTransactionDirection.Debit; }
                else { amountMinor = amt; direction = BankTransactionDirection.Credit; }
            }

            rows.Add(new ParsedRow(date, null, desc, refField, direction, amountMinor));
            close += direction == BankTransactionDirection.Credit ? amountMinor : -amountMinor;
            if (minD is null || date < minD) minD = date;
            if (maxD is null || date > maxD) maxD = date;
        }

        return Task.FromResult(new ParsedStatement(minD, maxD, open, close, rows));
    }

    private static List<string> SplitCsvRow(string row)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in row)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ',' && !inQuotes) { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(c);
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    private static string NormaliseColumnName(string s) => s.Trim().ToLowerInvariant();
    private static int IndexOf(List<string> header, string needle) => header.FindIndex(h => h == needle);

    private static bool TryParseDate(string? s, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        // ISO first
        if (DateOnly.TryParseExact(t, new[] { "yyyy-MM-dd", "yyyy/MM/dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        // dd/MM/yyyy and dd-MM-yyyy
        if (DateOnly.TryParseExact(t, new[] { "dd/MM/yyyy", "dd-MM-yyyy", "d/M/yyyy", "d-M-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        return DateOnly.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static long ParseAmountMinor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var t = s.Trim().Replace(",", string.Empty);
        if (string.IsNullOrEmpty(t)) return 0;
        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return 0;
        return (long)Math.Round(d * 100m, 0, MidpointRounding.ToEven);
    }
}
