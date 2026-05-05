namespace NickERP.Platform.Tenancy.Pilot;

/// <summary>
/// Sprint 38 — the five correctness gates that prove the system has
/// demonstrated end-to-end correctness for a pilot tenant. Vendor-neutral
/// IDs (no Ghana / FS6000 / ASE / ICUMS strings); each gate is a PROOF
/// that the system worked, not an artificial precondition (volume floors,
/// scanner-brand requirements, paper-cooperation checks). The system is
/// vendor-neutral by design (VP3) and federation-by-location (VP1) means
/// whichever site is stood up will self-select through these probes.
/// </summary>
public static class PilotReadinessGate
{
    /// <summary>At least one scanner has a registered adapter and a
    /// successful capability check (audit events
    /// <c>nickerp.inspection.scan_recorded</c> implies the
    /// <c>ScannerDeviceInstance</c> registered + the adapter ran the
    /// scan; vendor-neutral — fs6000/ase/mock all hit this same gate).</summary>
    public const string ScannerAdapter = "gate.scanner.adapter";

    /// <summary>Edge node completed full round-trip (capture → buffer →
    /// replay → audit row). Detected via a successful
    /// <c>inspection.scan.captured</c> audit event with
    /// <c>replay_source = "edge"</c> in its payload (set by
    /// <c>EdgeReplayEndpoint.AugmentPayload</c>).</summary>
    public const string EdgeRoundtrip = "gate.edge.roundtrip";

    /// <summary>At least one analyst decisioned a non-synthetic case
    /// end-to-end. Detected via a
    /// <c>nickerp.inspection.verdict_set</c> audit event whose case has
    /// <c>IsSynthetic = false</c>.</summary>
    public const string AnalystDecisionedRealCase = "gate.analyst.decisioned_real_case";

    /// <summary>External-system adapter completed a successful submission
    /// round-trip. Detected via an <c>OutboundSubmission</c> in
    /// <c>Status = "accepted"</c> with <c>LastAttemptAt</c> not null —
    /// vendor-neutral (icums/cmr/boe/post-hoc all hit this gate).</summary>
    public const string ExternalSystemRoundtrip = "gate.external_system.roundtrip";

    /// <summary>Multi-tenant RLS + isolation + cross-tenant gates hold
    /// under live traffic. Active probe — attempts cross-tenant reads
    /// AND verifies system-context audit register integrity AND verifies
    /// the cross-tenant export gate refuses an impersonation attempt.</summary>
    public const string MultiTenantInvariants = "gate.multi_tenant.invariants";

    /// <summary>The full set, in stable display order.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        ScannerAdapter,
        EdgeRoundtrip,
        AnalystDecisionedRealCase,
        ExternalSystemRoundtrip,
        MultiTenantInvariants,
    };
}

/// <summary>
/// Sprint 38 — observed state of a single readiness gate. <c>NotYetObserved</c>
/// is NOT a failure — the system simply hasn't seen the qualifying event
/// yet (e.g. no analyst has decisioned a non-synthetic case yet). The
/// dashboard surfaces "what's needed for this gate to pass" so the
/// operator can drive it.
/// </summary>
public enum PilotReadinessState
{
    /// <summary>The system has not yet observed a qualifying event for
    /// this gate. Surface "what's needed" guidance to the operator.</summary>
    NotYetObserved = 0,

    /// <summary>The gate has been satisfied — the system has observed at
    /// least one qualifying event. Captured timestamp + proof event id
    /// pinpoint the transition.</summary>
    Pass = 1,

    /// <summary>An active probe failed — for the multi-tenant invariants
    /// gate, this means a cross-tenant impersonation read returned rows,
    /// or the system-context register diverged from code, or the
    /// cross-tenant export gate did not refuse impersonation. The
    /// <c>Note</c> on the snapshot describes which sub-check failed.</summary>
    Fail = 2,
}
