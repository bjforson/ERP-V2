using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Sprint 31 / B5.1 — wall-clock SLA-window tracking row.
///
/// <para>
/// One row per (CaseId, WindowName) per case — mirrors v1 NSCIM's
/// <c>RecordCompletenessStatus</c> dwell-time / SLA columns but
/// expressed as discrete windows. The
/// <c>NickERP.Inspection.Application.Sla.ISlaTracker</c>
/// auto-opens windows on case creation and auto-closes them on
/// terminal-state transitions; the dashboard reads
/// <see cref="State"/> + <see cref="DueAt"/> to compute breach
/// summaries.
/// </para>
///
/// <para>
/// <b>Naming convention.</b> <see cref="WindowName"/> is dotted-lowercase
/// — e.g. <c>case.open_to_validated</c>, <c>case.validated_to_verdict</c>,
/// <c>case.verdict_to_submitted</c>. Vendor-neutral by design; CustomsGh
/// adapter modules can register their own windows (e.g.
/// <c>customsgh.boe_pull_to_match</c>) but those names land via plugin
/// registration, never in the engine's built-in defaults.
/// </para>
/// </summary>
public sealed class SlaWindow : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The case this window belongs to.</summary>
    public Guid CaseId { get; set; }

    /// <summary>Stable window identifier; dotted-lowercase.</summary>
    public string WindowName { get; set; } = string.Empty;

    /// <summary>When the window opened (clock-start).</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Configured deadline = <see cref="StartedAt"/> +
    /// the per-window budget. Stored at insert time so an admin
    /// changing the budget mid-window doesn't retroactively re-bucket
    /// already-open windows (audit-friendly).
    /// </summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>
    /// When the window closed (terminal state reached). Null while the
    /// window is still open.
    /// </summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Lifecycle bucket. Computed by
    /// <c>NickERP.Inspection.Application.Sla.ISlaTracker</c>:
    /// <list type="bullet">
    ///   <item><c>OnTime</c> — open and well within budget, OR closed before <see cref="DueAt"/>.</item>
    ///   <item><c>AtRisk</c> — open and within ~50% of <see cref="DueAt"/> elapsed.</item>
    ///   <item><c>Breached</c> — open past <see cref="DueAt"/>, OR closed after <see cref="DueAt"/>.</item>
    ///   <item><c>Closed</c> — terminal-state shortcut; replaces OnTime / Breached on close
    ///   to keep the dashboard's "closed lookups" cheap.</item>
    /// </list>
    /// </summary>
    public SlaWindowState State { get; set; } = SlaWindowState.OnTime;

    /// <summary>
    /// The snapshot budget (in minutes) at the time the window opened.
    /// Same value used to compute <see cref="DueAt"/>; persisted
    /// separately so the dashboard can tell at-a-glance which budget
    /// the row represents.
    /// </summary>
    public int BudgetMinutes { get; set; }

    /// <summary>
    /// Sprint 45 / Phase C — workload tier this window belongs to.
    /// Drives the per-tier budget defaults (see
    /// <c>SlaTracker.GetTierFirstReviewBudget</c> /
    /// <c>GetTierFinalBudget</c>) and the
    /// <c>QueueEscalatorWorker</c> auto-escalation thresholds.
    /// Defaults to <see cref="QueueTier.Standard"/> for backward
    /// compatibility; pre-Sprint-45 rows seed to Standard via the
    /// migration's column default.
    /// </summary>
    public QueueTier QueueTier { get; set; } = QueueTier.Standard;

    /// <summary>
    /// Sprint 45 / Phase C — true when the operator manually set the
    /// tier via the dashboard's "reclassify" action. The
    /// <c>QueueEscalatorWorker</c> auto-escalation respects this flag:
    /// a manually-tiered window is never auto-escalated. Useful when
    /// an operator has triaged a Standard case as Exception (no auto
    /// promotion) or as Urgent (no further auto-promotion).
    /// </summary>
    public bool QueueTierIsManual { get; set; }

    public long TenantId { get; set; }
}

/// <summary>
/// Sprint 45 / Phase C — workload tier for an
/// <see cref="SlaWindow"/>. Drives per-tier SLA budget defaults +
/// auto-escalation paths.
///
/// <para>
/// <b>Tier semantics.</b>
/// <list type="bullet">
///   <item><description><c>Standard</c> — typical case (15m first-review / 60m final). Auto-escalates to <c>High</c> after 30m open.</description></item>
///   <item><description><c>High</c> — flagged on intake (5m first-review / 30m final). Auto-escalates to <c>Urgent</c> after 60m open.</description></item>
///   <item><description><c>Urgent</c> — operator-actionable now (1m first-review / 10m final). Terminal — no further auto-escalation.</description></item>
///   <item><description><c>Exception</c> — indefinite hold (manual triage / blocked / awaiting authority response). No SLA budget enforced; manually-set tier never auto-escalates.</description></item>
///   <item><description><c>PostClearance</c> — non-time-critical follow-ups after the case has already cleared (24h budget). Manual triage path.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Tenant overrides.</b> The default budgets are hard-coded in
/// <c>SlaTracker</c>; per-tenant overrides come through
/// <c>TenantSetting</c> keys
/// (<c>inspection.queue.standard_first_review_minutes</c>,
/// <c>inspection.queue.standard_final_minutes</c>,
/// <c>inspection.queue.high_first_review_minutes</c> etc.).
/// </para>
/// </summary>
public enum QueueTier
{
    /// <summary>Default tier — typical case, normal SLA budget.</summary>
    Standard = 0,
    /// <summary>Flagged on intake — tighter SLA + auto-escalates to Urgent.</summary>
    High = 1,
    /// <summary>Operator-actionable now — tightest SLA, no further auto-escalation.</summary>
    Urgent = 2,
    /// <summary>Indefinite hold — no SLA budget enforced.</summary>
    Exception = 3,
    /// <summary>Post-clearance follow-up — long budget (24h), non-time-critical.</summary>
    PostClearance = 4
}

/// <summary>
/// Sprint 31 / B5.1 — lifecycle bucket for an
/// <see cref="SlaWindow"/>.
///
/// <para>
/// Aligned with v1 NSCIM analyst dashboard labels. The <c>Closed</c>
/// state is a "post-mortem" bucket — the dashboard only colours rows
/// that closed-after-due as <c>Breached</c>; rows closed-before-due
/// flip from <c>OnTime</c> straight to <c>Closed</c> so the dashboard
/// can hide them by default.
/// </para>
/// </summary>
public enum SlaWindowState
{
    /// <summary>Window open and tracking; under budget.</summary>
    OnTime = 0,

    /// <summary>Window open and ≥50% of budget elapsed without close.</summary>
    AtRisk = 10,

    /// <summary>Window open past the deadline, OR closed past the deadline.</summary>
    Breached = 20,

    /// <summary>Window closed under budget; dashboards hide by default.</summary>
    Closed = 30
}
