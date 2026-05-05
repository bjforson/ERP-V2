using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Completeness;

namespace NickERP.Inspection.Authorities.CustomsGh.Completeness;

/// <summary>
/// Sprint 48 / Phase C — FU-customsgh-completeness-requirements.
///
/// <para>
/// CustomsGh-specific completeness rule. For half-state CMR cases (those
/// where the regime is blank because the BOE/IM hasn't landed yet), the
/// half-state CMR fields must be populated — specifically the
/// port-of-loading and port-of-discharge inside the BOE-shaped payload's
/// <c>ManifestDetails.PortOfLoading</c> + <c>ManifestDetails.PortOfDischarge</c>
/// keys (the v1 NSCIM ICUMS shape used the same conventional names).
/// </para>
///
/// <para>
/// Behaviour:
/// <list type="bullet">
///   <item><b>Skip</b> when the case has no documents (pre-intake), or
///   when the cargo is not in the half-state phase (any document
///   carries a non-blank, non-CMR regime). The
///   <see cref="Validation.CmrPortRule"/> already covers the half-state
///   classification posture; this requirement is its completeness
///   counterpart.</item>
///   <item><b>Pass</b> when every half-state document has both ports
///   populated.</item>
///   <item><b>Incomplete</b> when at least one half-state document is
///   missing a port. Lists the missing ports in the
///   <see cref="CompletenessOutcome.MissingFields"/> bag so the
///   dashboard's per-case drill-down can show "needs port-of-loading"
///   on this case row.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>VP3 — Ghana data lives in the plugin.</b> Regime sets, document
/// shapes, and field names are owned by this adapter; the vendor-neutral
/// engine never sees them. CustomsGh-specific completeness requirements
/// live alongside the validation rules so deploys with no Ghana presence
/// don't carry the dead code.
/// </para>
/// </summary>
public sealed class CmrPortStateRequirement : ICompletenessRequirement
{
    /// <summary>Stable requirement id; matches the dotted-lowercase convention.</summary>
    public const string Id = "customsgh.cmr_port_state_complete";

    private readonly IOptions<CustomsGhValidationOptions> _options;

    public CmrPortStateRequirement(IOptions<CustomsGhValidationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string RequirementId => Id;
    public string Description =>
        "Half-state CMR cases must carry port-of-loading + port-of-discharge until the BOE/IM lands.";

    public CompletenessOutcome Evaluate(CompletenessContext context)
    {
        if (context.Documents.Count == 0)
            return CompletenessOutcome.Skip(Id, "case has no documents yet");

        var transitCodes = _options.Value.TransitRegimeCodes
            ?? new List<string> { "80", "88", "89" };

        // First pass: is any document in a non-half-state regime? If so,
        // half-state checks don't apply for this case — Skip.
        bool anyClassified = false;
        var halfStateDocs = new List<NickERP.Inspection.Core.Entities.AuthorityDocument>();

        foreach (var doc in context.Documents)
        {
            var reader = string.IsNullOrEmpty(doc.PayloadJson)
                ? null
                : BoePayloadReader.TryParse(doc.PayloadJson);
            var regime = (reader?.RegimeCode ?? string.Empty).Trim();
            var clearance = (reader?.ClearanceType ?? string.Empty).Trim();

            // Half-state shape per CmrPortRule.cs: blank regime AND
            // (blank clearance OR clearance == "CMR").
            var isHalfState =
                string.IsNullOrEmpty(regime)
                && (string.IsNullOrEmpty(clearance)
                    || string.Equals(clearance, GhCustoms.ClearanceTypes.Transit, StringComparison.OrdinalIgnoreCase));

            if (isHalfState)
            {
                halfStateDocs.Add(doc);
            }
            else
            {
                anyClassified = true;
            }
        }

        if (anyClassified)
        {
            return CompletenessOutcome.Skip(Id,
                "case has at least one classified-regime document — half-state CMR ports are no longer required.");
        }

        if (halfStateDocs.Count == 0)
        {
            return CompletenessOutcome.Skip(Id,
                "case has no half-state CMR documents to inspect.");
        }

        // Second pass — every half-state doc must carry both ports.
        var missing = new List<string>();
        var missingByDoc = 0;
        foreach (var doc in halfStateDocs)
        {
            var reader = BoePayloadReader.TryParse(doc.PayloadJson ?? string.Empty);
            var pol = reader?.PortOfLoading?.Trim();
            var pod = reader?.PortOfDischarge?.Trim();

            var docMissing = false;
            if (string.IsNullOrEmpty(pol))
            {
                if (!missing.Contains("port-of-loading", StringComparer.OrdinalIgnoreCase))
                    missing.Add("port-of-loading");
                docMissing = true;
            }
            if (string.IsNullOrEmpty(pod))
            {
                if (!missing.Contains("port-of-discharge", StringComparer.OrdinalIgnoreCase))
                    missing.Add("port-of-discharge");
                docMissing = true;
            }
            if (docMissing) missingByDoc++;
        }

        if (missing.Count == 0)
        {
            return new CompletenessOutcome(
                Id,
                CompletenessSeverity.Pass,
                "OK",
                MissingFields: null,
                Properties: new Dictionary<string, string>
                {
                    ["halfStateDocCount"] = halfStateDocs.Count.ToString(),
                });
        }

        return CompletenessOutcome.Incomplete(
            Id,
            $"{missingByDoc} half-state CMR document(s) missing port fields: {string.Join(", ", missing)}.",
            missingFields: missing,
            properties: new Dictionary<string, string>
            {
                ["halfStateDocCount"] = halfStateDocs.Count.ToString(),
                ["docsWithMissingPorts"] = missingByDoc.ToString()
            });
    }
}
