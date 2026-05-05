using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Sprint 31 / B5.2 — cross-record-scan detection result.
///
/// <para>
/// One row per case the
/// <c>NickERP.Inspection.Application.Detection.ICrossRecordScanDetector</c>
/// flagged as a multi-container candidate. Mirrors v1 NSCIM 2.15.4's
/// <c>CrossRecordScans</c> table but expressed in vendor-neutral terms
/// — rather than carrying Container1/Container2/BOE columns directly,
/// the row carries a JSON payload that names every detected
/// child-subject so detectors can report N-way splits without a
/// pair-of-columns schema explosion.
/// </para>
///
/// <para>
/// Lifecycle:
/// <list type="bullet">
///   <item><c>Pending</c> — detector flagged, awaiting analyst review.</item>
///   <item><c>Confirmed</c> — analyst agreed the case is multi-container; ready for split.</item>
///   <item><c>Split</c> — child cases created via
///   <c>CaseWorkflowService.SplitCaseAsync</c>; <see cref="SplitCaseIdsJson"/>
///   carries the new case ids.</item>
///   <item><c>Dismissed</c> — analyst rejected the detection (false positive).</item>
/// </list>
/// </para>
/// </summary>
public sealed class CrossRecordDetection : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The original case the detector examined.</summary>
    public Guid CaseId { get; set; }

    /// <summary>When the detector ran.</summary>
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>
    /// The detector's stable version code. Bumped whenever the
    /// detection algorithm changes so the audit trail can trace which
    /// rule fired the row.
    /// </summary>
    public string DetectorVersion { get; set; } = "v1";

    /// <summary>Lifecycle state — see class docs.</summary>
    public CrossRecordDetectionState State { get; set; } = CrossRecordDetectionState.Pending;

    /// <summary>
    /// JSON payload of detected subjects. Shape:
    /// <c>[{"subjectIdentifier": "...", "evidence": "..."}, ...]</c>.
    /// Vendor-neutral — no FS6000/ASE container-number columns. The
    /// detector chooses the keys; the admin UI renders them as a list.
    /// </summary>
    public string DetectedSubjectsJson { get; set; } = "[]";

    /// <summary>
    /// JSON payload of split-target case ids, populated when
    /// <see cref="State"/>=<see cref="CrossRecordDetectionState.Split"/>.
    /// Shape: <c>["guid", "guid", ...]</c>. Null for Pending /
    /// Confirmed / Dismissed states.
    /// </summary>
    public string? SplitCaseIdsJson { get; set; }

    /// <summary>Free-form analyst note; populated on Confirmed / Dismissed.</summary>
    public string? Notes { get; set; }

    /// <summary>The user who confirmed / dismissed / executed the split.</summary>
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>When the analyst confirmed / dismissed / split. Null while Pending.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    public long TenantId { get; set; }
}

/// <summary>
/// Sprint 31 / B5.2 — lifecycle state for
/// <see cref="CrossRecordDetection"/>. See class doc on
/// <see cref="CrossRecordDetection"/> for the transition map.
/// </summary>
public enum CrossRecordDetectionState
{
    /// <summary>Detector flagged; awaiting analyst review.</summary>
    Pending = 0,

    /// <summary>Analyst confirmed; ready to split.</summary>
    Confirmed = 10,

    /// <summary>Analyst rejected (false positive).</summary>
    Dismissed = 20,

    /// <summary>Child cases created via SplitCaseAsync; SplitCaseIdsJson populated.</summary>
    Split = 30
}
