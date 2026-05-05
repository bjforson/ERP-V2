namespace NickERP.Inspection.Core.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — outcome bucket for an
/// <see cref="ICompletenessRequirement"/> evaluation.
///
/// <para>
/// Three buckets, deliberately small. <see cref="Pass"/> = the requirement
/// is satisfied; <see cref="PartiallyComplete"/> = some required artifacts
/// or data points exist but the threshold was not met (the analyst can
/// still proceed but the dashboard will flag the gap); <see cref="Incomplete"/>
/// = the requirement is hard-missing.
/// </para>
///
/// <para>
/// <see cref="Skip"/> is a separate posture used when the requirement
/// cannot run for legitimate reasons (e.g. case has no scans yet, so the
/// "required scan artifact" requirement abstains rather than firing a
/// false negative). Mirrors the v1 NSCIM completeness-severity taxonomy
/// so analyst-facing labels survive v1 → v2.
/// </para>
/// </summary>
public enum CompletenessSeverity
{
    /// <summary>Requirement satisfied.</summary>
    Pass = 0,

    /// <summary>Requirement partially satisfied — surfaced to operator, not blocking.</summary>
    PartiallyComplete = 10,

    /// <summary>Requirement hard-missing — counts toward the SLA breach + blocks "Complete" rollup.</summary>
    Incomplete = 20,

    /// <summary>Requirement abstained (insufficient data). Audit-only.</summary>
    Skip = 30
}
