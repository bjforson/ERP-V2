using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Authorities.CustomsGh.Validation;

/// <summary>
/// Sprint 28 / B4 — half-state CMR + transit posture for Customs Ghana.
///
/// <para>
/// CMR (Cross-border Manifest Reference, ICUMS terminology) documents
/// arrive ahead of the BOE/IM declaration — the "half-state" period
/// where the cargo has been pre-declared but the customs regime hasn't
/// been classified yet. v1's port-match and Fyco rules quietly abstained
/// when the regime was blank because that's the half-state shape.
/// </para>
///
/// <para>
/// In v2 we want a separate, explicit posture for the half-state +
/// transit families so admin tooling can show the analyst "this case
/// is correctly half-state — wait for the BOE before flagging port /
/// direction issues" rather than letting the silence look like a
/// passing rule. This rule emits:
/// </para>
///
/// <list type="bullet">
///   <item>
///     <b>Skip</b> when the case has no documents yet, or every document
///     is half-state (blank ClearanceType + blank RegimeCode).
///   </item>
///   <item>
///     <b>Info</b> when the case has documents and at least one is
///     transit (regime ∈ {80, 88, 89}) — informational so the analyst
///     knows transit cargo follows different physical-flow rules
///     (arrives by sea, leaves by road).
///   </item>
///   <item>
///     <b>Pass</b> when every document carries a non-half-state regime /
///     ClearanceType. Lets the broader rule pack take over.
///   </item>
/// </list>
///
/// <para>
/// Per the 2026-05-04 operator clarification: transit cargo physically
/// can't have a fyco=EXPORT event; that's why FycoDirectionRule does NOT
/// skip transit. CmrPortRule's transit Info posture is purely
/// informational — it doesn't gate any other rule.
/// </para>
/// </summary>
public sealed class CmrPortRule : IValidationRule
{
    /// <summary>Stable rule identifier.</summary>
    public const string Id = "customsgh.cmr_port_state";

    private readonly IOptions<CustomsGhValidationOptions> _options;

    public CmrPortRule(IOptions<CustomsGhValidationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string RuleId => Id;
    public string Description =>
        "Surface half-state CMR + transit-regime cases as a distinct posture so the analyst doesn't conflate silence-by-design with passing.";

    public ValidationOutcome Evaluate(ValidationContext context)
    {
        if (context.Documents.Count == 0)
            return ValidationOutcome.Skip(Id, "case has no documents yet");

        var transitCodes = _options.Value.TransitRegimeCodes
            ?? new List<string> { "80", "88", "89" };

        var halfStateDocs = 0;
        var transitDocs = 0;
        var classifiedDocs = 0;
        string? exemplarTransitRegime = null;
        string? exemplarTransitDocRef = null;

        foreach (var doc in context.Documents)
        {
            if (string.IsNullOrEmpty(doc.PayloadJson))
            {
                halfStateDocs++;
                continue;
            }
            var reader = BoePayloadReader.TryParse(doc.PayloadJson);
            if (reader is null)
            {
                halfStateDocs++;
                continue;
            }

            var regime = (reader.RegimeCode ?? "").Trim();
            var clearance = (reader.ClearanceType ?? "").Trim();

            if (string.IsNullOrEmpty(regime)
                && (string.IsNullOrEmpty(clearance)
                    || string.Equals(clearance, GhCustoms.ClearanceTypes.Transit, StringComparison.OrdinalIgnoreCase)))
            {
                halfStateDocs++;
                continue;
            }

            if (!string.IsNullOrEmpty(regime)
                && transitCodes.Any(c => string.Equals(c, regime, StringComparison.OrdinalIgnoreCase)))
            {
                transitDocs++;
                exemplarTransitRegime ??= regime;
                exemplarTransitDocRef ??= doc.ReferenceNumber;
                classifiedDocs++;
                continue;
            }

            classifiedDocs++;
        }

        if (transitDocs > 0)
        {
            return new ValidationOutcome(
                Id,
                ValidationSeverity.Info,
                $"Case carries {transitDocs} transit document(s) (regime {exemplarTransitRegime}). "
                + "Transit cargo arrives by vessel and departs Ghana overland — different physical flow than imports/exports.",
                new Dictionary<string, string>
                {
                    ["transitDocCount"] = transitDocs.ToString(),
                    ["classifiedDocCount"] = classifiedDocs.ToString(),
                    ["halfStateDocCount"] = halfStateDocs.ToString(),
                    ["exemplarRegime"] = exemplarTransitRegime ?? "",
                    ["exemplarDocRef"] = exemplarTransitDocRef ?? ""
                });
        }

        if (classifiedDocs == 0 && halfStateDocs > 0)
        {
            return ValidationOutcome.Skip(
                Id,
                $"all {halfStateDocs} document(s) are half-state (no regime + ClearanceType blank/CMR); waiting for BOE.");
        }

        return ValidationOutcome.Pass(Id);
    }
}
