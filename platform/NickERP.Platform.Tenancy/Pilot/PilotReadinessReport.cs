namespace NickERP.Platform.Tenancy.Pilot;

/// <summary>
/// Sprint 38 — top-level readiness snapshot for a single tenant. One row
/// per gate. Written to <c>tenancy.pilot_readiness_snapshots</c> on every
/// refresh; the table is append-only (no UPDATE on existing rows) so the
/// dashboard can show "first observed" + "latest observed" without losing
/// history.
/// </summary>
/// <param name="TenantId">Pilot tenant whose readiness was probed.</param>
/// <param name="ObservedAt">When this report snapshot was generated.</param>
/// <param name="Gates">One entry per <see cref="PilotReadinessGate"/>.
/// Entries are returned in <see cref="PilotReadinessGate.All"/> order so
/// the dashboard can render a stable column layout.</param>
public sealed record PilotReadinessReport(
    long TenantId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<PilotReadinessGateResult> Gates);

/// <summary>
/// Sprint 38 — observation of a single gate.
/// </summary>
/// <param name="GateId">Stable id from <see cref="PilotReadinessGate"/>.</param>
/// <param name="State">Pass / Fail / NotYetObserved.</param>
/// <param name="ObservedAt">When the probe ran.</param>
/// <param name="ProofEventId">For Pass — the audit-event id that
/// proves the gate transition; null otherwise.</param>
/// <param name="Note">Human-readable explanation:
/// <list type="bullet">
///   <item><description>For NotYetObserved — "what's needed to pass".</description></item>
///   <item><description>For Fail — which sub-check failed (active-probe gates).</description></item>
///   <item><description>For Pass — usually null; may carry "first observed" hint.</description></item>
/// </list></param>
public sealed record PilotReadinessGateResult(
    string GateId,
    PilotReadinessState State,
    DateTimeOffset ObservedAt,
    Guid? ProofEventId,
    string? Note);
