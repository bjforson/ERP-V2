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

    /// <summary>
    /// Sprint 36 / FU-completeness-percent-requirements — first
    /// percent-based built-in. "case has at least N% of the expected
    /// scan-artifact channels per scan" (where N defaults to 0.85 and
    /// can be overridden per-tenant via
    /// <c>tenancy.tenant_completeness_settings.MinThreshold</c>).
    /// Vendor-neutral coverage definition: each scan is expected to
    /// emit a baseline set of <see cref="NickERP.Inspection.Core.Entities.ScanArtifact.ArtifactKind"/>
    /// values; the requirement compares the observed distinct kinds
    /// against the expected count and flips Incomplete below threshold.
    /// </summary>
    public const string RequiredImageCoverage = "required.image_coverage";
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

/// <summary>
/// Sprint 36 / FU-completeness-percent-requirements — first percent-based
/// built-in. "case has at least N of the expected distinct scan-artifact
/// channels", where N is computed against
/// <see cref="ExpectedDistinctArtifactKinds"/> (default 4, mirroring the
/// canonical Primary / SideView / Material / IR set most adapters
/// emit) and the threshold is decimal in [0, 1] (default 0.85 = 85%).
///
/// <para>
/// Behaviour:
/// <list type="bullet">
///   <item><b>Skip</b> when the case has zero scans (pre-scan; firing
///   here would be noise on every freshly-opened case).</item>
///   <item><b>Incomplete</b> when the observed coverage is strictly
///   below the resolved threshold. The outcome's
///   <see cref="CompletenessOutcome.Properties"/> bag carries
///   <c>observedValue</c> (decimal coverage ratio in [0,1]) +
///   <c>distinctKinds</c> + <c>expectedKinds</c>; the engine reads
///   <c>observedValue</c> back to emit
///   <c>inspection.completeness.threshold_used</c>.</item>
///   <item><b>Pass</b> at or above threshold.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Vendor-neutral.</b> "ArtifactKind" is the adapter-defined channel
/// label (Primary, SideView, Material, IR, ROI, etc.); no Ghana strings
/// land here. Per-tenant adapters that emit a richer baseline (e.g.
/// dual-energy gives a separate "DualEnergy" channel) can either lift
/// <c>ExpectedDistinctArtifactKinds</c> via configuration in a future
/// sprint, or override the threshold per-tenant to match their
/// equipment's baseline.
/// </para>
/// </summary>
public sealed class RequiredImageCoverageRequirement : ICompletenessRequirement
{
    /// <summary>
    /// Default expected baseline of distinct
    /// <see cref="NickERP.Inspection.Core.Entities.ScanArtifact.ArtifactKind"/>
    /// values per scan. Derived from the canonical
    /// <c>{ Primary, SideView, Material, IR }</c> set most adapters
    /// emit (Sprint 13 ScanArtifact contract). Pilot data may push
    /// this to 5/6 once the multi-angle camera adapters land — wired
    /// then via the same per-tenant <c>MinThreshold</c> override.
    /// </summary>
    public const int ExpectedDistinctArtifactKinds = 4;

    /// <summary>
    /// Default threshold of 0.85 (= 85%): a 4-channel scanner that
    /// emitted 3/4 channels (75%) flips Incomplete; a 6-channel
    /// scanner that emitted 5/6 (~83%) flips Incomplete; a scan with
    /// 4/4 (100%) Passes. Override per-tenant via
    /// <c>tenancy.tenant_completeness_settings.MinThreshold</c> when
    /// pilot data tightens or loosens the bar.
    /// </summary>
    public const decimal DefaultMinThresholdValue = 0.85m;

    public string RequirementId => BuiltInCompletenessRequirementIds.RequiredImageCoverage;
    public string Description => "Case has the expected fraction of distinct scan-artifact channels per scan.";
    public decimal? DefaultMinThreshold => DefaultMinThresholdValue;

    public CompletenessOutcome Evaluate(CompletenessContext context)
    {
        // Pre-scan skip — same posture as RequiredScanArtifact: a case
        // without any Scan rows is in pre-intake, not a coverage
        // failure.
        if (context.Scans.Count == 0)
            return CompletenessOutcome.Skip(RequirementId, "case has no scans yet");

        // No artifacts at all means the scan recorded but ingestion
        // hasn't materialised any channels yet — Incomplete with 0%
        // coverage so the dashboard's "image coverage" column lights up.
        if (context.ScanArtifacts.Count == 0)
        {
            return CompletenessOutcome.Incomplete(
                RequirementId,
                "Case has scans but no artifact channels.",
                missingFields: new[] { "scan-artifact-channels" },
                properties: new Dictionary<string, string>
                {
                    ["observedValue"] = "0",
                    ["distinctKinds"] = "0",
                    ["expectedKinds"] = ExpectedDistinctArtifactKinds.ToString(),
                    ["threshold"] = (context.ThresholdFor(RequirementId) ?? DefaultMinThresholdValue)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        var threshold = context.ThresholdFor(RequirementId) ?? DefaultMinThresholdValue;

        // Coverage = (distinct ArtifactKind values across this case's
        // artifacts) / ExpectedDistinctArtifactKinds. Capped at 1.0 so a
        // 5-channel scanner reporting 5 distinct kinds against an
        // expected 4 doesn't read 1.25.
        var distinctKinds = context.ScanArtifacts
            .Select(a => a.ArtifactKind ?? string.Empty)
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        decimal coverage = ExpectedDistinctArtifactKinds <= 0
            ? 1m
            : Math.Min(1m, (decimal)distinctKinds / ExpectedDistinctArtifactKinds);

        var props = new Dictionary<string, string>
        {
            ["observedValue"] = coverage.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["distinctKinds"] = distinctKinds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["expectedKinds"] = ExpectedDistinctArtifactKinds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["threshold"] = threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        if (coverage < threshold)
        {
            return CompletenessOutcome.Incomplete(
                RequirementId,
                $"Coverage {coverage:0.##} below threshold {threshold:0.##}.",
                missingFields: new[] { "scan-artifact-channels" },
                properties: props);
        }

        return new CompletenessOutcome(
            RequirementId,
            CompletenessSeverity.Pass,
            "OK",
            MissingFields: null,
            Properties: props);
    }
}
