namespace NickERP.Inspection.Core.Validation;

/// <summary>
/// Sprint 28 — vendor-neutral validation-rule contract.
///
/// <para>
/// One implementation = one rule. Rules are resolved via DI (so plugins,
/// adapters, and the inspection-host project can all contribute rules)
/// and discovered by the engine (in <c>NickERP.Inspection.Application.Validation</c>) at construction.
/// Every implementation MUST be stateless across calls — the engine may
/// reuse the same instance for multiple cases concurrently.
/// </para>
///
/// <para>
/// <see cref="RuleId"/> is the stable identifier for config + audit. It
/// is dotted-lowercase by convention (e.g. <c>"required.scan_artifact"</c>,
/// <c>"customsgh.port_match"</c>, <c>"customsgh.fyco_direction"</c>).
/// Within a deployment it MUST be unique; the engine throws on
/// duplicate-ID registration.
/// </para>
///
/// <para>
/// Synchronous evaluation is intentional. Rules look up only data already
/// loaded into the <see cref="ValidationContext"/>; if a rule needs more,
/// the engine builder loads it eagerly. This keeps the per-case eval
/// path cheap (~µs each) and the audit trail per-rule.
/// </para>
/// </summary>
public interface IValidationRule
{
    /// <summary>
    /// Stable rule identifier. Dotted-lowercase. MUST match the
    /// <see cref="ValidationOutcome.RuleId"/> the rule emits.
    /// </summary>
    string RuleId { get; }

    /// <summary>Short human description for the admin UI.</summary>
    string Description { get; }

    /// <summary>
    /// Evaluate the rule against the supplied context and return the
    /// outcome. The engine calls this once per case; rules MUST NOT
    /// throw — return a Skip with the failure reason if the rule can't
    /// produce a verdict, or an Error with the diagnostic if the rule
    /// detects a violation.
    /// </summary>
    ValidationOutcome Evaluate(ValidationContext context);
}
