namespace NickERP.Inspection.Core.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — vendor-neutral completeness-requirement contract.
///
/// <para>
/// One implementation = one requirement. Requirements are resolved via
/// DI (so the inspection-host project + CustomsGh / NigeriaCustoms /
/// other adapters can all contribute requirements) and discovered by
/// the engine at construction. Every implementation MUST be stateless
/// across calls — the engine may reuse the same instance for multiple
/// cases concurrently.
/// </para>
///
/// <para>
/// <see cref="RequirementId"/> is the stable identifier for config + audit.
/// Dotted-lowercase by convention (e.g. <c>"required.scan_artifact"</c>,
/// <c>"required.customs_declaration"</c>, <c>"required.analyst_decision"</c>).
/// Within a deployment it MUST be unique; the engine throws on
/// duplicate-ID registration.
/// </para>
///
/// <para>
/// Synchronous evaluation is intentional — mirrors the
/// <see cref="Validation.IValidationRule"/> contract from Sprint 28.
/// Requirements look up only data already loaded into the
/// <see cref="CompletenessContext"/>; if a requirement needs more, the
/// engine builder loads it eagerly. This keeps the per-case evaluation
/// path cheap (~µs each) and the audit trail per-requirement.
/// </para>
/// </summary>
public interface ICompletenessRequirement
{
    /// <summary>
    /// Stable requirement identifier. Dotted-lowercase. MUST match the
    /// <see cref="CompletenessOutcome.RequirementId"/> the requirement
    /// emits.
    /// </summary>
    string RequirementId { get; }

    /// <summary>Short human description for the admin UI.</summary>
    string Description { get; }

    /// <summary>
    /// Evaluate the requirement against the supplied context and return
    /// the outcome. The engine calls this once per case; requirements
    /// MUST NOT throw — return a Skip with the failure reason if the
    /// requirement can't produce a verdict, or an Incomplete with the
    /// diagnostic if the requirement detects a gap.
    /// </summary>
    CompletenessOutcome Evaluate(CompletenessContext context);
}
