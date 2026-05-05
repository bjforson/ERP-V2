namespace NickERP.Platform.Tenancy.Pilot;

/// <summary>
/// Sprint 38 — module-facing API for the pilot-acceptance correctness
/// probe. The dashboard at <c>/admin/pilot-readiness</c> calls
/// <see cref="GetReadinessAsync"/> on demand and a periodic
/// <c>BackgroundService</c> calls it every 60s by default. Each call
/// runs all 5 gates and writes one append-only row per
/// <c>(TenantId, GateId)</c> to <c>tenancy.pilot_readiness_snapshots</c>.
/// </summary>
/// <remarks>
/// <para>
/// Probe semantics: NotYetObserved is NOT a failure — the system simply
/// hasn't seen the qualifying event yet. The dashboard surfaces "what's
/// needed" guidance for these so the operator can drive them.
/// </para>
/// <para>
/// Gate 5 (multi-tenant invariants) is the marquee active probe: it
/// attempts cross-tenant reads under tenant A's context for tenant B's
/// rows and verifies RLS rejects them, scans the
/// <c>SetSystemContext</c> caller register for drift against code, and
/// checks the cross-tenant export gate refuses an impersonation attempt.
/// </para>
/// <para>
/// Failure of a gate's probe code does NOT throw — exceptions are caught
/// and surfaced as <see cref="PilotReadinessState.Fail"/> with the
/// reason in the <see cref="PilotReadinessGateResult.Note"/>. The
/// dashboard never crashes because an internal probe died.
/// </para>
/// </remarks>
public interface IPilotReadinessService
{
    /// <summary>
    /// Run all 5 gates against the supplied tenant, persist a snapshot
    /// row per gate, and return the report. Idempotent across refreshes
    /// — running this back-to-back produces back-to-back snapshot rows
    /// with the same observed state if no new audit events arrived.
    /// </summary>
    /// <param name="tenantId">Pilot tenant to probe.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PilotReadinessReport> GetReadinessAsync(long tenantId, CancellationToken ct = default);
}
