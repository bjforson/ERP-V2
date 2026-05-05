using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Authorities.CustomsGh.Validation;

/// <summary>
/// Sprint 28 / B4 — Customs Ghana Fyco import/export direction rule.
///
/// <para>
/// Ports v1's <c>ContainerValidationService.ValidateFycoImportExportAsync</c>
/// (lines 840-886) into the vendor-neutral engine. v1 enforced
/// <c>FS6000Scan.FycoPresent</c> (export-flag) matched <c>BOE.ClearanceType</c>
/// (IM/EX/CMR; CMR was Skip — pre-BOE state).
/// </para>
///
/// <para>
/// v2 broadens the parser to handle the data-entry pattern operators
/// observed in the wild — <c>FycoPresent</c> values like
/// <c>WAYBILL/EXPORT</c>, <c>WAYBIL/EXPORT</c>, <c>EPORT</c> defeat v1's
/// narrow <c>1/Y/YES</c> parser. The default
/// <see cref="CustomsGhValidationOptions.FycoExportPattern"/> regex
/// matches the broader set (case-insensitive, typo-tolerant).
/// </para>
///
/// <para>
/// <strong>Sprint 37 / FU-fyco-export-pattern-eport — typo coverage.</strong>
/// The default export regex matches the canonical spelling
/// <c>EXPORT</c> plus three observed single-letter typos:
/// <list type="bullet">
///   <item><description><c>EXORT</c> (missing P)</description></item>
///   <item><description><c>EPORT</c> (missing X)</description></item>
///   <item><description><c>EXPRT</c> (missing O)</description></item>
/// </list>
/// Truly-exotic free-text (<c>EXPROT</c> anagram, <c>X-PORT</c>
/// hyphenation, etc.) is intentionally NOT matched — those need an
/// operator call to confirm they're real export indicators rather than
/// random data noise. Adding more typos here later is cheap;
/// over-matching silently flips Skip outcomes into Errors and is the
/// more expensive regression to walk back.
/// </para>
///
/// <para>
/// Direction also resolves through the BOE's <c>RegimeCode</c>:
/// import = 40/50/61/62/70/90, export = 10/19/20/34/39, transit = 80/88/89.
/// Per the 2026-05-04 operator clarification, transit is NOT skipped — a
/// fyco=EXPORT scan on transit cargo is a real anomaly (transit cargo
/// physically leaves Ghana overland, not by sea).
/// </para>
///
/// <para>
/// Skip postures: no Fyco-bearing scan, no decoder-recognised regime,
/// scan with both export- and import-matching Fyco (data nonsense).
/// </para>
/// </summary>
public sealed class FycoDirectionRule : IValidationRule
{
    /// <summary>Stable rule identifier — matches v1's <c>GH-FYCO</c> code.</summary>
    public const string Id = "customsgh.fyco_direction";

    /// <summary>The metadata key adapters use for the Fyco flag (<c>scanner.fyco_present</c>).</summary>
    public const string FycoMetadataKey = "scanner.fyco_present";

    /// <summary>
    /// Default export-match regex — Sprint 37 tightening. Matches the
    /// canonical <c>EXPORT</c> plus three observed single-letter typos
    /// (<c>EXORT</c>, <c>EPORT</c>, <c>EXPRT</c>) inside any
    /// surrounding free-text (e.g. <c>WAYBILL/EXPORT</c>,
    /// <c>WAYBIL/EXPORT</c>, <c>WAYBILL/EPORT</c>). Also matches the
    /// v1 narrow flags <c>1</c>, <c>Y</c>, <c>YES</c>, <c>TRUE</c> as
    /// standalone values. Word-boundary protected so <c>NOEXPORT</c>
    /// is matched as <c>EXPORT</c> only via the boundary — there's no
    /// negation handling, but operators don't write the negative
    /// inline today. Case-insensitive.
    /// </summary>
    public const string DefaultExportPattern = @"(?i)\b(export|exort|eport|exprt)\b|^\s*[1Yy]\s*$|^\s*(yes|true)\s*$";

    /// <summary>
    /// Default import-match regex. Matches <c>IMPORT</c> case-
    /// insensitively or the v1 narrow flags <c>0</c>, <c>N</c>,
    /// <c>NO</c>, <c>FALSE</c> as standalone values.
    /// </summary>
    public const string DefaultImportPattern = @"(?i)\bimport\b|^\s*[0Nn]\s*$|^\s*(no|false)\s*$";

    private readonly IOptions<CustomsGhValidationOptions> _options;
    private readonly Regex _exportRegex;
    private readonly Regex _importRegex;

    public FycoDirectionRule(IOptions<CustomsGhValidationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // Compile once at construction. Patterns come from configuration
        // so a busted regex shouldn't crash the engine — fall back to the
        // memoised default patterns if compilation fails, log nothing
        // (the rule is a no-op anyway).
        _exportRegex = TryCompile(options.Value.FycoExportPattern, DefaultExportPattern);
        _importRegex = TryCompile(options.Value.FycoImportPattern, DefaultImportPattern);
    }

    public string RuleId => Id;
    public string Description =>
        "Scanner Fyco flag must agree with BOE direction (ClearanceType + RegimeCode).";

    public ValidationOutcome Evaluate(ValidationContext context)
    {
        if (context.Scans.Count == 0)
            return ValidationOutcome.Skip(Id, "case has no scans yet");

        // Find the latest scan that exposes a Fyco flag in its artifact
        // metadata. Adapters that don't surface FycoPresent simply don't
        // populate the key — rule degrades to a Skip rather than an
        // Error.
        var fycoValue = ReadLatestFycoValue(context);
        if (fycoValue is null)
            return ValidationOutcome.Skip(Id, "no scan in this case carries a Fyco flag");

        var isExport = _exportRegex.IsMatch(fycoValue);
        var isImport = _importRegex.IsMatch(fycoValue);
        if (isExport && isImport)
            return ValidationOutcome.Skip(Id,
                $"Fyco value '{fycoValue}' matched both export and import patterns; refusing to call it.");
        if (!isExport && !isImport)
            return ValidationOutcome.Skip(Id,
                $"Fyco value '{fycoValue}' didn't match either export or import patterns.");
        var fycoDirection = isExport ? "export" : "import";

        var opts = _options.Value;
        // Walk the documents looking for a recognised regime / clearance
        // type. The first document with a usable signal decides the
        // outcome — matches the v1 single-BOE-per-case assumption.
        foreach (var doc in context.Documents)
        {
            if (string.IsNullOrEmpty(doc.PayloadJson)) continue;
            var reader = BoePayloadReader.TryParse(doc.PayloadJson);
            if (reader is null) continue;

            var (boeDirection, source) = ResolveBoeDirection(reader, opts);
            if (boeDirection is null) continue;

            if (string.Equals(boeDirection, fycoDirection, StringComparison.OrdinalIgnoreCase))
                return ValidationOutcome.Pass(Id);

            return ValidationOutcome.Error(
                Id,
                $"Fyco direction mismatch: scanner reported '{fycoValue}' (interpreted as {fycoDirection}) "
                + $"but BOE indicates {boeDirection} via {source}.",
                new Dictionary<string, string>
                {
                    ["fycoValue"] = fycoValue,
                    ["fycoDirection"] = fycoDirection,
                    ["boeDirection"] = boeDirection,
                    ["boeSignalSource"] = source,
                    ["clearanceType"] = reader.ClearanceType ?? "",
                    ["regimeCode"] = reader.RegimeCode ?? "",
                    ["documentType"] = doc.DocumentType ?? "",
                    ["documentReference"] = doc.ReferenceNumber ?? ""
                });
        }

        return ValidationOutcome.Skip(Id,
            "no document on the case exposes a usable ClearanceType / RegimeCode");
    }

    /// <summary>
    /// Pull the most-recent Fyco flag from the scan-artifact metadata
    /// dictionary. Adapters serialize ScanArtifact.MetadataJson as a
    /// flat <c>{"key":"value"}</c> dictionary; we walk artifacts in
    /// reverse-time order so the latest scan wins on key conflict.
    /// </summary>
    private static string? ReadLatestFycoValue(ValidationContext context)
    {
        // Most recent scan first.
        foreach (var scan in context.Scans.OrderByDescending(s => s.CapturedAt))
        {
            var artifacts = context.ScanArtifacts.Where(a => a.ScanId == scan.Id);
            foreach (var a in artifacts)
            {
                if (string.IsNullOrEmpty(a.MetadataJson)) continue;
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(a.MetadataJson);
                    if (dict is not null && dict.TryGetValue(FycoMetadataKey, out var v)
                        && !string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
                catch (JsonException) { /* skip malformed metadata */ }
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve a BOE's direction. Prefer RegimeCode (more specific +
    /// includes transit); fall back to ClearanceType (IM/EX) when
    /// regime is missing or unrecognised. Returns the source string the
    /// rule can include in the error message for human triage.
    /// </summary>
    private static (string? Direction, string Source) ResolveBoeDirection(
        BoePayloadReader reader,
        CustomsGhValidationOptions opts)
    {
        var regime = (reader.RegimeCode ?? "").Trim();
        if (!string.IsNullOrEmpty(regime))
        {
            if (opts.ImportRegimeCodes.Any(c => string.Equals(c, regime, StringComparison.OrdinalIgnoreCase)))
                return ("import", $"regime {regime}");
            if (opts.ExportRegimeCodes.Any(c => string.Equals(c, regime, StringComparison.OrdinalIgnoreCase)))
                return ("export", $"regime {regime}");
            if (opts.TransitRegimeCodes.Any(c => string.Equals(c, regime, StringComparison.OrdinalIgnoreCase)))
                return ("import", $"regime {regime} (transit; treated as import-direction for fyco)");
        }

        var ct = (reader.ClearanceType ?? "").Trim();
        if (string.Equals(ct, "IM", StringComparison.OrdinalIgnoreCase))
            return ("import", "ClearanceType=IM");
        if (string.Equals(ct, "EX", StringComparison.OrdinalIgnoreCase))
            return ("export", "ClearanceType=EX");
        // CMR / blank → no direction yet (half-state). The CmrPortRule
        // owns that posture; the fyco rule abstains.
        return (null, "no usable signal");
    }

    private static Regex TryCompile(string pattern, string fallback)
    {
        try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException)
        {
            return new Regex(fallback, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
    }
}
