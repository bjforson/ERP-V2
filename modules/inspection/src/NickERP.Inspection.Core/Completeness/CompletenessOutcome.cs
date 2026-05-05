namespace NickERP.Inspection.Core.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — single-requirement evaluation outcome. Returned by
/// <see cref="ICompletenessRequirement.Evaluate"/> and aggregated by the
/// engine.
///
/// <para>
/// <see cref="RequirementId"/> is the stable code (dotted-lowercase, e.g.
/// <c>"required.scan_artifact"</c>) that identifies the requirement
/// across config (per-tenant disable flags) + audit events
/// (<c>properties-&gt;&gt;'requirementId'</c>) + the analyst-facing UI.
/// MUST match <see cref="ICompletenessRequirement.RequirementId"/>.
/// </para>
///
/// <para>
/// <see cref="MissingFields"/> is a free-form list of data-points the
/// requirement expected but did not find. Mirrors v1's
/// <c>ContainerCompleteness.MissingFields</c> shape so the v2 rollup
/// dashboard can show the same per-row drill-down. Use semantic field
/// names (e.g. <c>"scan-artifact"</c>, <c>"customs-declaration"</c>) —
/// no Ghana-specific values land here; CustomsGh-specific requirements
/// live in adapter projects.
/// </para>
///
/// <para>
/// <see cref="Properties"/> is a free-form bag for requirement-specific
/// facts the dashboard or audit trail might want — e.g. for a "minimum
/// scan-count" requirement, the actually-observed count and the
/// configured threshold. Engines persist this bag verbatim into the
/// audit event payload + the Finding's LocationInImageJson. Keep it
/// small — it ends up in audit-log row counts.
/// </para>
/// </summary>
public sealed record CompletenessOutcome(
    string RequirementId,
    CompletenessSeverity Severity,
    string Message,
    IReadOnlyList<string>? MissingFields = null,
    IReadOnlyDictionary<string, string>? Properties = null)
{
    /// <summary>
    /// Construct a Pass outcome — requirement satisfied. Non-empty message
    /// is convention for Pass too so the dashboard can show "OK" or a
    /// short justification.
    /// </summary>
    public static CompletenessOutcome Pass(string requirementId, string message = "OK")
        => new(requirementId, CompletenessSeverity.Pass, message);

    /// <summary>
    /// Construct a PartiallyComplete outcome — some but not all expected
    /// artifacts present.
    /// </summary>
    public static CompletenessOutcome Partial(
        string requirementId,
        string message,
        IReadOnlyList<string>? missingFields = null,
        IReadOnlyDictionary<string, string>? properties = null)
        => new(requirementId, CompletenessSeverity.PartiallyComplete, message, missingFields, properties);

    /// <summary>
    /// Construct an Incomplete outcome — requirement hard-missing.
    /// Counts toward SLA breach + blocks the case-rollup "Complete"
    /// state.
    /// </summary>
    public static CompletenessOutcome Incomplete(
        string requirementId,
        string message,
        IReadOnlyList<string>? missingFields = null,
        IReadOnlyDictionary<string, string>? properties = null)
        => new(requirementId, CompletenessSeverity.Incomplete, message, missingFields, properties);

    /// <summary>
    /// Construct a Skip outcome — requirement abstained because the case
    /// lacked the data the requirement needs. Audit-only — never a Finding.
    /// </summary>
    public static CompletenessOutcome Skip(string requirementId, string reason)
        => new(requirementId, CompletenessSeverity.Skip, reason);
}

/// <summary>
/// Aggregated output of one completeness-engine pass against a case.
/// Mirrors the v1 <c>RecordCompletenessStatus.Status</c> rollup
/// (Complete / PartiallyComplete / Incomplete / Pending) but expressed
/// in vendor-neutral terms.
/// </summary>
public sealed record CompletenessEvaluationResult(
    Guid CaseId,
    IReadOnlyList<CompletenessOutcome> Outcomes)
{
    /// <summary>True when at least one outcome is Incomplete.</summary>
    public bool HasIncomplete => Outcomes.Any(o => o.Severity == CompletenessSeverity.Incomplete);

    /// <summary>True when at least one outcome is PartiallyComplete.</summary>
    public bool HasPartial => Outcomes.Any(o => o.Severity == CompletenessSeverity.PartiallyComplete);

    /// <summary>
    /// Roll up the per-requirement severities into the case-level state.
    /// Hierarchy: any Incomplete → <see cref="CompletenessSeverity.Incomplete"/>;
    /// otherwise any PartiallyComplete → <see cref="CompletenessSeverity.PartiallyComplete"/>;
    /// otherwise <see cref="CompletenessSeverity.Pass"/>. Skip outcomes
    /// don't influence the rollup (they're audit-only).
    /// </summary>
    public CompletenessSeverity RollupSeverity
    {
        get
        {
            if (HasIncomplete) return CompletenessSeverity.Incomplete;
            if (HasPartial) return CompletenessSeverity.PartiallyComplete;
            return CompletenessSeverity.Pass;
        }
    }

    /// <summary>Findings-eligible outcomes (excludes Skip).</summary>
    public IEnumerable<CompletenessOutcome> Findings =>
        Outcomes.Where(o => o.Severity != CompletenessSeverity.Skip);

    /// <summary>Flatten every outcome's MissingFields into one ordered set.</summary>
    public IReadOnlyList<string> AllMissingFields =>
        Outcomes
            .Where(o => o.MissingFields is { Count: > 0 })
            .SelectMany(o => o.MissingFields!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
