using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Per-<see cref="ScannerDeviceInstance"/> threshold profile (§6.5.3).
/// v1 treated every scanner identically with five compile-time constants;
/// v2 treats thresholds as runtime configuration versioned alongside the
/// model artifacts they parameterize.
///
/// <para>
/// Workflow: a stats job (auto-tune) or admin (manual) emits a
/// <see cref="ScannerThresholdProfileStatus.Proposed"/> row with a candidate
/// <see cref="ValuesJson"/>. The admin reviews the rationale, clicks
/// approve, and the row enters
/// <see cref="ScannerThresholdProfileStatus.Shadow"/> for a 24 h shadow
/// run on 5 % of traffic; if the shadow gate passes, the row promotes to
/// <see cref="ScannerThresholdProfileStatus.Active"/> and the previously
/// active row for the same scanner moves to
/// <see cref="ScannerThresholdProfileStatus.Superseded"/>. Failed shadow
/// goes to <see cref="ScannerThresholdProfileStatus.Rejected"/>.
/// </para>
///
/// <para>
/// A unique partial index on
/// <c>(ScannerDeviceInstanceId) WHERE Status = 'active'</c> enforces at
/// most one active profile per scanner. <see cref="Version"/> is monotonic
/// per scanner — bootstrap rows stamp <c>0</c>, every subsequent proposal
/// increments.
/// </para>
///
/// <para>
/// <see cref="ValuesJson"/> validates against the
/// <c>ScannerDeviceType.threshold_schema</c> declared by the
/// <c>IScannerAdapter</c> implementation — schema-per-scanner-type, not a
/// global one-size-fits-all schema (§6.5.3 locked decision).
/// </para>
/// </summary>
public sealed class ScannerThresholdProfile : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The scanner this profile applies to.</summary>
    public Guid ScannerDeviceInstanceId { get; set; }
    public ScannerDeviceInstance? ScannerDeviceInstance { get; set; }

    /// <summary>Monotonic per <see cref="ScannerDeviceInstanceId"/>; bootstrap = 0.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Threshold values as JSON. Validated against the
    /// <c>IScannerAdapter</c>'s declared
    /// <c>ScannerDeviceType.threshold_schema</c>; non-conforming rows are
    /// stamped <see cref="ScannerThresholdProfileStatus.Rejected"/>.
    /// </summary>
    public string ValuesJson { get; set; } = "{}";

    /// <summary>Lifecycle state — see workflow on the class summary.</summary>
    public ScannerThresholdProfileStatus Status { get; set; } = ScannerThresholdProfileStatus.Proposed;

    /// <summary>When this profile became (or will become) active. Null until it passes the shadow gate.</summary>
    public DateTimeOffset? EffectiveFrom { get; set; }

    /// <summary>When this profile was superseded by a newer active row. Null while live.</summary>
    public DateTimeOffset? EffectiveTo { get; set; }

    /// <summary>Where the proposal came from.</summary>
    public ScannerThresholdProposalSource ProposedBy { get; set; } = ScannerThresholdProposalSource.AutoTune;

    /// <summary>
    /// Why this proposal exists — auto-tune emits stats, dispersion,
    /// confidence; manual proposals stamp the admin's free-form note.
    /// </summary>
    public string ProposalRationaleJson { get; set; } = "{}";

    /// <summary>The admin who clicked Approve. Null while still <see cref="ScannerThresholdProfileStatus.Proposed"/>.</summary>
    public Guid? ApprovedByUserId { get; set; }

    /// <summary>When the admin clicked Approve.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>When the 24 h shadow run started.</summary>
    public DateTimeOffset? ShadowStartedAt { get; set; }

    /// <summary>When the shadow gate completed (pass or fail).</summary>
    public DateTimeOffset? ShadowCompletedAt { get; set; }

    /// <summary>
    /// Shadow gate verdict + evidence — verdict ∈ <c>{pass, fail, inconclusive}</c>,
    /// review-event count, precision/recall deltas vs current active profile.
    /// </summary>
    public string? ShadowOutcomeJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public long TenantId { get; set; }
}

/// <summary>Lifecycle state of a <see cref="ScannerThresholdProfile"/>.</summary>
public enum ScannerThresholdProfileStatus
{
    /// <summary>Auto-tune or admin emitted — awaiting admin approval.</summary>
    Proposed = 0,

    /// <summary>Approved; running on a 5 % traffic slice for the 24 h validation window.</summary>
    Shadow = 10,

    /// <summary>Live — every scan on this scanner uses these values. At most one per scanner via partial unique index.</summary>
    Active = 20,

    /// <summary>Was active, replaced by a newer active row.</summary>
    Superseded = 30,

    /// <summary>Failed the shadow gate or schema-validation.</summary>
    Rejected = 40
}

/// <summary>What kind of process produced a <see cref="ScannerThresholdProfile"/>.</summary>
public enum ScannerThresholdProposalSource
{
    /// <summary>Day-one cutover row, stamped from v1 hardcoded constants for behavioural parity (§6.5.4).</summary>
    Bootstrap = 0,

    /// <summary>Weekly cron's auto-tune emitted this proposal from rolling per-scanner stats.</summary>
    AutoTune = 10,

    /// <summary>An admin authored this profile by hand via the manual-tune UI (§6.5.7 phase 1).</summary>
    Manual = 20
}
