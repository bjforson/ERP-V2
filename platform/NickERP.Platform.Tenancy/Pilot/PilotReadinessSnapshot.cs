namespace NickERP.Platform.Tenancy.Pilot;

/// <summary>
/// Sprint 38 — append-only snapshot row in
/// <c>tenancy.pilot_readiness_snapshots</c>. Every refresh of the
/// readiness probe writes one row per gate per tenant. The dashboard reads
/// the latest row per <c>(TenantId, GateId)</c> via a window-function
/// query. Rows are NEVER updated — historical state is preserved so
/// transitions Pass→Fail→Pass are auditable.
/// </summary>
/// <remarks>
/// <para>
/// Cross-tenant by design (admin tooling); same posture as
/// <c>tenant_purge_log</c> and <c>tenant_export_requests</c> — lives in
/// the <c>tenancy</c> schema and is intentionally NOT under RLS.
/// </para>
/// <para>
/// Why not RLS-scoped: the dashboard at <c>/admin/pilot-readiness</c> is
/// the single consumer; the page is gated by an admin role at the route
/// layer, the same way the existing tenants admin page is. Adding RLS
/// here would force a system-context flip every time the dashboard
/// renders, broadening the system-context audit register for marginal
/// safety gain.
/// </para>
/// </remarks>
public sealed class PilotReadinessSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant the gate was probed for.</summary>
    public long TenantId { get; set; }

    /// <summary>Stable gate id from <see cref="PilotReadinessGate"/>.</summary>
    public string GateId { get; set; } = string.Empty;

    /// <summary>Pass / Fail / NotYetObserved at the time this row was written.</summary>
    public PilotReadinessState State { get; set; }

    /// <summary>When the probe ran.</summary>
    public DateTimeOffset ObservedAt { get; set; }

    /// <summary>For Pass — the audit-event id that proved the
    /// transition. Null for NotYetObserved + Fail.</summary>
    public Guid? ProofEventId { get; set; }

    /// <summary>Optional explanation; "what's needed" for NotYetObserved,
    /// failure reason for Fail, occasional first-observed hint for Pass.</summary>
    public string? Note { get; set; }
}
