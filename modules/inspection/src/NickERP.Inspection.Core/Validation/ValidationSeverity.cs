namespace NickERP.Inspection.Core.Validation;

/// <summary>
/// Sprint 28 — severity bucket for an <see cref="IValidationRule"/> outcome.
///
/// <para>
/// Three buckets, deliberately small. <see cref="Info"/> is informational
/// only and never blocks a workflow transition; <see cref="Warning"/> is
/// surfaced to the analyst and feeds the verdict-suggestion logic but
/// doesn't block; <see cref="Error"/> is the strongest signal and the
/// only level that blocks submission when paired with a strict-mode rule
/// configuration. <see cref="Skip"/> is a separate posture (rule abstained,
/// not enough data) and is recorded as an audit-only row — never a Finding.
/// </para>
///
/// <para>
/// Aligned with the v1 NSCIM <c>BusinessRuleResultSeverity</c> taxonomy
/// so analyst-facing labels (Info/Warning/Error) carry the same meaning
/// across v1→v2 — operators learning the v2 UI shouldn't have to re-learn
/// what "Warning" implies.
/// </para>
/// </summary>
public enum ValidationSeverity
{
    /// <summary>Informational only. Recorded but not surfaced as a Finding.</summary>
    Info = 0,

    /// <summary>Surfaced to analyst; may influence verdict suggestion. Not blocking.</summary>
    Warning = 10,

    /// <summary>Strongest signal. Blocks submission in strict mode.</summary>
    Error = 20,

    /// <summary>Rule abstained (insufficient data). Audit-only — never a Finding.</summary>
    Skip = 30
}
