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

    public long TenantId { get; set; }
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
