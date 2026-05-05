using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Completeness;

namespace NickERP.Inspection.Authorities.CustomsGh.Completeness;

/// <summary>
/// Sprint 48 / Phase C — FU-customsgh-completeness-requirements.
///
/// <para>
/// CustomsGh-specific completeness rule. For declarations under regimes
/// 70 (warehousing — import family), 40 (direct import), and 90
/// (re-export), the case must carry the matching authority document
/// type:
/// <list type="bullet">
///   <item>Regime 40 / 70 (import) — at least one BOE document.</item>
///   <item>Regime 90 (re-export — export family) — at least one BOE
///   document (export side files BOEs too in Ghana).</item>
///   <item>Regime 80 (transit) — at least one Manifest / CMR document
///   per memory <c>feedback_regime80_cmr_is_transit.md</c>: regime 80
///   stays CMR-shaped even after the BOE lands.</item>
/// </list>
/// </para>
///
/// <para>
/// Behaviour:
/// <list type="bullet">
///   <item><b>Skip</b> when the case has no documents (pre-intake), or
///   when no document carries one of the regimes in
///   <see cref="GatedRegimeCodes"/>.</item>
///   <item><b>Pass</b> when every gated-regime document has its required
///   companion type present.</item>
///   <item><b>Incomplete</b> when at least one gated-regime document is
///   missing its companion. Lists the regimes + expected document types
///   in the <see cref="CompletenessOutcome.MissingFields"/> bag.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>VP3 — Ghana data lives in the plugin.</b> Regime sets + document
/// type strings are owned by this adapter; vendor-neutral core never
/// sees them.
/// </para>
/// </summary>
public sealed class RegimeSpecificDocumentsRequirement : ICompletenessRequirement
{
    /// <summary>Stable requirement id; matches the dotted-lowercase convention.</summary>
    public const string Id = "customsgh.regime_specific_documents";

    /// <summary>
    /// Document type strings the requirement looks for. Mirror v1's
    /// ICUMS ingestion vocabulary so the dashboard's "missing-doc" drill-
    /// downs match what operators already see.
    /// </summary>
    public static class DocumentTypes
    {
        public const string Boe = "BOE";
        public const string Manifest = "Manifest";
        public const string Cmr = "CMR";
    }

    /// <summary>
    /// Regimes this requirement gates. Anything outside this set
    /// triggers Skip — vendor-neutral completeness pulls only happen
    /// when the regime maps to one of these explicit operational
    /// classes (per memory <c>reference_port_match_rules_enabled_2026_05_02.md</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> GatedRegimeCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "40", "70", "80", "90" };

    private readonly IOptions<CustomsGhValidationOptions> _options;

    public RegimeSpecificDocumentsRequirement(IOptions<CustomsGhValidationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string RequirementId => Id;
    public string Description =>
        "Regime-classified declarations must carry the matching authority document type (BOE for import / re-export; Manifest for transit).";

    public CompletenessOutcome Evaluate(CompletenessContext context)
    {
        if (context.Documents.Count == 0)
            return CompletenessOutcome.Skip(Id, "case has no documents yet");

        var transitCodes = _options.Value.TransitRegimeCodes
            ?? new List<string> { "80", "88", "89" };

        // Find every gated-regime document on the case + the regime
        // each one carries.
        var gatedRegimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in context.Documents)
        {
            if (string.IsNullOrEmpty(doc.PayloadJson)) continue;
            var reader = BoePayloadReader.TryParse(doc.PayloadJson);
            if (reader is null) continue;
            var regime = (reader.RegimeCode ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(regime)) continue;
            if (GatedRegimeCodes.Contains(regime))
            {
                gatedRegimes.Add(regime);
            }
        }

        if (gatedRegimes.Count == 0)
        {
            return CompletenessOutcome.Skip(Id,
                "case has no documents under regimes 40/70/80/90 — completeness rule does not apply.");
        }

        // For each gated regime, check the matching companion document
        // type is present somewhere on the case. Companion-type
        // mapping:
        //   40 / 70 — BOE
        //   90 — BOE (export side still files BOEs)
        //   80 — Manifest / CMR (transit stays CMR-shaped per memory)
        var missing = new List<string>();
        var props = new Dictionary<string, string>();

        foreach (var regime in gatedRegimes)
        {
            var isTransit = transitCodes.Contains(regime, StringComparer.OrdinalIgnoreCase);
            var expected = isTransit
                ? new[] { DocumentTypes.Manifest, DocumentTypes.Cmr }
                : new[] { DocumentTypes.Boe };

            // Pass if any expected document type is present.
            var hasCompanion = expected.Any(type =>
                context.Documents.Any(d =>
                    string.Equals(d.DocumentType, type, StringComparison.OrdinalIgnoreCase)));

            props[$"regime_{regime}_required"] = string.Join("|", expected);
            props[$"regime_{regime}_present"] = hasCompanion ? "true" : "false";

            if (!hasCompanion)
            {
                var label = isTransit
                    ? $"regime-{regime}-manifest-or-cmr"
                    : $"regime-{regime}-boe";
                missing.Add(label);
            }
        }

        if (missing.Count == 0)
        {
            return new CompletenessOutcome(
                Id,
                CompletenessSeverity.Pass,
                "OK",
                MissingFields: null,
                Properties: props);
        }

        return CompletenessOutcome.Incomplete(
            Id,
            $"Case missing required document(s) for {missing.Count} regime(s): {string.Join(", ", missing)}.",
            missingFields: missing,
            properties: props);
    }
}
