using NickERP.Inspection.Core.Completeness;

namespace NickERP.Inspection.Application.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — vendor-neutral, built-in completeness requirements.
///
/// <para>
/// <b>Strict no-Ghana-data rule.</b> These requirements MUST stay
/// vendor-neutral — port codes, regime codes, Fyco values, and any
/// other Ghana-specific data live in
/// <c>NickERP.Inspection.Authorities.CustomsGh.Completeness</c>. The
/// requirement IDs below are the v1 NSCIM analogues expressed in
/// vendor-neutral terms (the v1 service was
/// <c>ContainerCompletenessService</c>; v1 had FS6000 / ICUMS strings
/// baked into it — those land in adapter projects).
/// </para>
/// </summary>
public static class BuiltInCompletenessRequirementIds
{
    /// <summary>
    /// "case must have at least one scan artifact attached before the
    /// rollup considers it complete." Encodes the v1 invariant that a
    /// declaration without a scan is a bookkeeping case, not a finished
    /// inspection. Mirrors the
    /// <see cref="NickERP.Inspection.Application.Validation.BuiltInRuleIds.RequiredScanArtifact"/>
    /// validation-rule shape, but at the completeness/rollup layer.
    /// </summary>
    public const string RequiredScanArtifact = "required.scan_artifact";

    /// <summary>
    /// "case must have at least one customs declaration document
    /// attached." Looks at the count of
    /// <see cref="NickERP.Inspection.Core.Entities.AuthorityDocument"/>
    /// rows, not their type — vendor-specific document-type assertions
    /// land in adapter projects.
    /// </summary>
    public const string RequiredCustomsDeclaration = "required.customs_declaration";

    /// <summary>
    /// "case must carry a recorded analyst decision (verdict or
    /// analyst-review row) before the rollup flips to Complete." Closes
    /// the v1 NSCIM parity gap where a completed scan + completed
    /// document fetch was treated as Complete even when no analyst had
    /// touched the case.
    /// </summary>
    public const string RequiredAnalystDecision = "required.analyst_decision";
}

/// <summary>
/// "case has at least one scan artifact attached." Skip on no scans
/// (case is pre-scan); Incomplete when scans exist but no
/// <see cref="NickERP.Inspection.Core.Entities.ScanArtifact"/> rows
/// back them.
/// </summary>
public sealed class RequiredScanArtifactRequirement : ICompletenessRequirement
{
    public string RequirementId => BuiltInCompletenessRequirementIds.RequiredScanArtifact;
    public string Description => "Case must carry at least one scan artifact.";

    public CompletenessOutcome Evaluate(CompletenessContext context)
    {
        if (context.Scans.Count == 0)
            return CompletenessOutcome.Skip(RequirementId, "case has no scans yet");

        if (context.ScanArtifacts.Count > 0)
            return CompletenessOutcome.Pass(RequirementId);

        return CompletenessOutcome.Incomplete(
            RequirementId,
            "Case has scans but no artifact rows — ingestion likely failed.",
            missingFields: new[] { "scan-artifact" },
            properties: new Dictionary<string, string>
            {
                ["scanCount"] = context.Scans.Count.ToString(),
                ["artifactCount"] = "0"
            });
    }
}

/// <summary>
/// "case has at least one authority document attached." Used to gate
/// the case-rollup from advancing past PartiallyComplete without proof
/// of customs paperwork.
/// </summary>
public sealed class RequiredCustomsDeclarationRequirement : ICompletenessRequirement
{
    public string RequirementId => BuiltInCompletenessRequirementIds.RequiredCustomsDeclaration;
    public string Description => "Case must have at least one authority document attached.";

    public CompletenessOutcome Evaluate(CompletenessContext context)
    {
        if (context.Documents.Count > 0)
            return CompletenessOutcome.Pass(RequirementId);

        return CompletenessOutcome.Incomplete(
            RequirementId,
            "Case has no customs declaration documents attached.",
            missingFields: new[] { "customs-declaration" },
            properties: new Dictionary<string, string> { ["documentCount"] = "0" });
    }
}

/// <summary>
/// "case has a recorded analyst decision — verdict or analyst-review."
/// Skip until the case has both a scan and a document (otherwise the
/// requirement would always fire on freshly-opened cases). Counts as
/// PartiallyComplete (not Incomplete) when the artifacts are in place
/// but the analyst hasn't yet set a verdict — the case is still
/// progressing through the workflow, not stuck.
/// </summary>
public sealed class RequiredAnalystDecisionRequirement : ICompletenessRequirement
{
    public string RequirementId => BuiltInCompletenessRequirementIds.RequiredAnalystDecision;
    public string Description => "Case must carry a recorded analyst decision before rolling up Complete.";

    public CompletenessOutcome Evaluate(CompletenessContext context)
    {
        // Pre-decision skip: no scans + no documents means the case is
        // still in intake; firing this requirement would be noise.
        if (context.Scans.Count == 0 && context.Documents.Count == 0)
            return CompletenessOutcome.Skip(RequirementId, "case is pre-intake");

        if (context.HasAnalystDecision)
            return CompletenessOutcome.Pass(RequirementId);

        return CompletenessOutcome.Partial(
            RequirementId,
            "Case has scan + document but no analyst decision yet.",
            missingFields: new[] { "analyst-decision" },
            properties: new Dictionary<string, string>
            {
                ["scanCount"] = context.Scans.Count.ToString(),
                ["documentCount"] = context.Documents.Count.ToString()
            });
    }
}
