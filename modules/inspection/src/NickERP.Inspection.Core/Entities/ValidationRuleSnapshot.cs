using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Sprint 48 / Phase B — FU-validation-rule-evaluation-snapshot.
///
/// <para>
/// Snapshot of one rule's outcome from a <c>ValidationEngine</c>
/// evaluation. Allows <c>/cases/{id}</c> to hydrate the validation pane
/// on cold reload without re-running the engine — the analyst doesn't
/// have to click "Re-run validation rules" every time they navigate
/// back to the case.
/// </para>
///
/// <para>
/// One row per <em>(<see cref="CaseId"/>, <see cref="RuleId"/>)</em> per
/// evaluation event. Mirrors <see cref="RuleEvaluation"/> (the
/// authority-rules-pack analog) in shape but at the per-rule grain
/// instead of per-authority grain. Snapshots are append-only — the
/// engine writes a fresh row on every evaluation; the most-recent-per-
/// (case, rule) is the natural read path. Never UPDATE; never DELETE.
/// History lives implicitly via the multiple rows per (case, rule)
/// pair, ordered by <see cref="EvaluatedAt"/> descending.
/// </para>
///
/// <para>
/// <see cref="Severity"/> is the int representation of
/// <see cref="NickERP.Inspection.Core.Validation.ValidationSeverity"/>
/// (Info=0, Warning=10, Error=20, Skip=30 — see the enum's stable
/// values). <see cref="Outcome"/> mirrors the lower-case severity
/// string used by the engine's Finding writes (<c>"info"</c>,
/// <c>"warning"</c>, <c>"error"</c>, <c>"skip"</c>) so admin/queries
/// can match without re-translating.
/// </para>
///
/// <para>
/// <see cref="Properties"/> carries the rule-specific properties bag
/// from the <see cref="NickERP.Inspection.Core.Validation.ValidationOutcome.Properties"/>
/// dictionary, serialised to jsonb. Empty bag → empty json object
/// <c>'{}'::jsonb</c>; the engine never writes null.
/// </para>
/// </summary>
public sealed class ValidationRuleSnapshot : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }

    /// <summary>Stable rule id — matches <see cref="NickERP.Inspection.Core.Validation.IValidationRule.RuleId"/>.</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>Int representation of the rule's <see cref="NickERP.Inspection.Core.Validation.ValidationSeverity"/>.</summary>
    public int Severity { get; set; }

    /// <summary>Lowercased severity string (info/warning/error/skip) — mirrors the Finding row.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Rule's emitted message; nullable when the rule supplied none.</summary>
    public string? Message { get; set; }

    /// <summary>jsonb-serialised properties bag; empty object when the rule emitted none.</summary>
    public string PropertiesJson { get; set; } = "{}";

    /// <summary>When this snapshot was written (UTC).</summary>
    public DateTimeOffset EvaluatedAt { get; set; }

    public long TenantId { get; set; }
}
