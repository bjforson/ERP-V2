namespace NickERP.Inspection.Core.Validation;

/// <summary>
/// Sprint 28 — single-rule outcome record. Returned by <see cref="IValidationRule.Evaluate"/>
/// and aggregated by the engine.
///
/// <para>
/// <see cref="RuleId"/> is the stable code (e.g. <c>"required.scan_artifact"</c>,
/// <c>"customsgh.port_match"</c>) that identifies the rule across config
/// (per-tenant disable flags), audit events (<c>properties->>'ruleId'</c>),
/// and the analyst-facing UI. It MUST match
/// <see cref="IValidationRule.RuleId"/>.
/// </para>
///
/// <para>
/// <see cref="Severity"/> drives whether the engine writes a
/// <see cref="Entities.Finding"/> row (Info / Warning / Error) or only an
/// audit event (Skip). Skip is a separate posture — used when a rule
/// cannot run for legitimate reasons (e.g. blank regime → half-state CMR
/// for the CmrPortRule).
/// </para>
///
/// <para>
/// <see cref="Properties"/> is a free-form bag for rule-specific facts
/// the analyst UI / drill-down might want — e.g. for the port-match
/// rule, the actually-observed port code and the expected port code.
/// Engines persist this bag verbatim into the audit event payload and
/// into the Finding's LocationInImageJson (under a <c>"properties"</c>
/// key). Keep it small — it ends up in audit log row counts.
/// </para>
/// </summary>
public sealed record ValidationOutcome(
    string RuleId,
    ValidationSeverity Severity,
    string Message,
    IReadOnlyDictionary<string, string>? Properties = null)
{
    /// <summary>
    /// Construct an Info outcome — typically used for "rule passed" rows
    /// when the engine is configured to emit pass-events for analytics.
    /// </summary>
    public static ValidationOutcome Pass(string ruleId, string message = "OK")
        => new(ruleId, ValidationSeverity.Info, message);

    /// <summary>
    /// Construct a Warning outcome — non-blocking but surfaced to the
    /// analyst.
    /// </summary>
    public static ValidationOutcome Warn(string ruleId, string message,
        IReadOnlyDictionary<string, string>? properties = null)
        => new(ruleId, ValidationSeverity.Warning, message, properties);

    /// <summary>
    /// Construct an Error outcome — blocks submission in strict mode.
    /// </summary>
    public static ValidationOutcome Error(string ruleId, string message,
        IReadOnlyDictionary<string, string>? properties = null)
        => new(ruleId, ValidationSeverity.Error, message, properties);

    /// <summary>
    /// Construct a Skip outcome — rule abstained because the case lacked
    /// the data the rule needs. Recorded as an audit event but not as a
    /// Finding.
    /// </summary>
    public static ValidationOutcome Skip(string ruleId, string reason)
        => new(ruleId, ValidationSeverity.Skip, reason);
}

/// <summary>
/// Aggregated output of an engine run (see <c>ValidationEngine</c> in
/// <c>NickERP.Inspection.Application.Validation</c>). Carries the per-rule
/// outcomes plus a quick has-errors flag for the workflow gate.
/// </summary>
public sealed record ValidationEngineResult(
    Guid CaseId,
    IReadOnlyList<ValidationOutcome> Outcomes)
{
    /// <summary>True when at least one outcome has Severity=Error.</summary>
    public bool HasErrors => Outcomes.Any(o => o.Severity == ValidationSeverity.Error);

    /// <summary>True when at least one outcome has Severity=Warning.</summary>
    public bool HasWarnings => Outcomes.Any(o => o.Severity == ValidationSeverity.Warning);

    /// <summary>Convenience filter — only Findings-eligible outcomes (Info/Warning/Error).</summary>
    public IEnumerable<ValidationOutcome> Findings =>
        Outcomes.Where(o => o.Severity != ValidationSeverity.Skip);
}
