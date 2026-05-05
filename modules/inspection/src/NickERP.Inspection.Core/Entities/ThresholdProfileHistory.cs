using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Sprint 41 / Phase B — append-only history of threshold-value changes
/// per scanner. One row per change, never updated or deleted. Mirrors
/// the audit posture of <c>audit.events</c> (no DELETE grant) so the
/// "reversibility" requirement of doc-analysis Table 21 (threshold
/// changes must be role-controlled, logged and reversible) has a
/// dedicated, queryable trail.
///
/// <para>
/// <b>Why a separate table?</b> <see cref="ScannerThresholdProfile"/>
/// already records every proposal as a versioned row, but the threshold
/// resolver-facing "active" state is a single row per scanner. When a
/// proposal goes through Proposed → Shadow → Active and supersedes the
/// previous Active, the per-class threshold values move atomically with
/// the row flip. This history table records the per-class delta itself
/// (what threshold was changed, what was the prior value, what's the
/// new value) which makes "show me every change to scanner X's
/// material-anomaly threshold over the last 30 days" answerable in one
/// query rather than reconstructing diffs from JSON values.
/// </para>
///
/// <para>
/// <b>Auto-emitted from <c>ThresholdAdminService.ApproveAsync</c>.</b>
/// On approval (Proposed → Shadow), the service compares the new
/// <c>ValuesJson</c> against the prior Active row's <c>ValuesJson</c>
/// for the same scanner and emits one row per differing key. If there
/// is no prior Active row (bootstrap / first proposal), the OldThreshold
/// column is null. Audit event <c>nickerp.inspection.threshold_changed</c>
/// fires alongside, carrying the same diff plus the actor and rationale.
/// </para>
/// </summary>
public sealed class ThresholdProfileHistory : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The scanner whose threshold changed.</summary>
    public Guid ScannerDeviceInstanceId { get; set; }

    /// <summary>
    /// Free-form model identifier — for v2 this is typically the inference
    /// model the threshold gates (<c>fs6000.material_anomaly</c> /
    /// <c>onnx.threat-detection-v3</c>). Mirrors the
    /// <c>ScannerDeviceType.threshold_schema</c> top-level keys.
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Free-form class identifier within the model — typically the threat
    /// class or anomaly bucket the threshold scores against
    /// (<c>weapon</c> / <c>narcotics</c> / <c>contraband-currency</c>).
    /// </summary>
    public string ClassId { get; set; } = string.Empty;

    /// <summary>
    /// Prior threshold value. Null on the bootstrap / first-proposal row
    /// (no prior Active to compare against).
    /// </summary>
    public double? OldThreshold { get; set; }

    /// <summary>New threshold value applied by the change.</summary>
    public double NewThreshold { get; set; }

    /// <summary>When the change was committed.</summary>
    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>Who committed the change. Null for system-emitted (bootstrap / auto-tune-as-bootstrap) rows.</summary>
    public Guid? ChangedByUserId { get; set; }

    /// <summary>Optional reason / rationale snippet — typically the proposal's manual-tune note.</summary>
    public string? Reason { get; set; }

    public long TenantId { get; set; }
}
