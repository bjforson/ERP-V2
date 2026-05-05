using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Application.Sla;

/// <summary>
/// Sprint 31 / B5.1 — SLA-window tracker contract. Lives in
/// <c>Application/Sla</c> so the
/// <c>NickERP.Inspection.Web.Services.CaseWorkflowService</c> can call
/// it inline at state transitions without a hard reference on the
/// concrete tracker (eases test wiring).
///
/// <para>
/// Vendor-neutral. Built-in window names (lifted from the case
/// workflow): <c>case.open_to_validated</c>,
/// <c>case.validated_to_verdict</c>, <c>case.verdict_to_submitted</c>.
/// Adapter modules may register their own window names through plugin
/// registration; the tracker treats them opaquely.
/// </para>
/// </summary>
public interface ISlaTracker
{
    /// <summary>
    /// Open the requested SLA windows for a case. Idempotent — if a
    /// window with the same (TenantId, CaseId, WindowName) already
    /// exists and is still open, the call is a no-op. Each opened
    /// window's <see cref="SlaWindow.DueAt"/> is computed from the
    /// per-tenant budget (or the engine default when no per-tenant
    /// override exists).
    /// </summary>
    Task<IReadOnlyList<SlaWindow>> OpenWindowsAsync(
        Guid caseId,
        IReadOnlyCollection<string> windowNames,
        DateTimeOffset openedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Open the standard "case opened" windows
    /// (<c>case.open_to_validated</c>, <c>case.validated_to_verdict</c>,
    /// <c>case.verdict_to_submitted</c>) for a freshly-created case.
    /// Convenience wrapper over <see cref="OpenWindowsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<SlaWindow>> OpenStandardWindowsAsync(
        Guid caseId,
        DateTimeOffset openedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Close all open windows for a case (called on the terminal-state
    /// transition Closed/Cancelled). Each window's terminal
    /// <see cref="SlaWindow.State"/> is computed from
    /// (<see cref="SlaWindow.DueAt"/>, closeAt) — closed-before-due flips
    /// to <c>Closed</c>; closed-after-due flips to <c>Breached</c>.
    /// </summary>
    Task<int> CloseAllOpenWindowsAsync(
        Guid caseId,
        DateTimeOffset closedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Close one named window for a case (called on intermediate
    /// transitions, e.g. Open→Validated closes
    /// <c>case.open_to_validated</c>). Idempotent — closing an
    /// already-closed window is a no-op; closing a non-existent window
    /// is a no-op.
    /// </summary>
    Task<bool> CloseWindowAsync(
        Guid caseId,
        string windowName,
        DateTimeOffset closedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Recompute <see cref="SlaWindow.State"/> on every still-open
    /// window for a case. Call from a periodic worker (or from the
    /// dashboard query path) so AtRisk → Breached transitions don't
    /// require a state change to flip. Returns the count of rows
    /// updated.
    /// </summary>
    Task<int> RefreshStatesAsync(Guid caseId, DateTimeOffset asOf, CancellationToken ct = default);
}
