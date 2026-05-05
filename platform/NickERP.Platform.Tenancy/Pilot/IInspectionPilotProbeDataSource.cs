namespace NickERP.Platform.Tenancy.Pilot;

/// <summary>
/// Sprint 43 — abstraction the <c>PilotReadinessService</c> calls into for
/// inspection-domain reads it needs (IsSynthetic flag, OutboundSubmission
/// status). The concrete implementation lives in
/// <c>apps/portal/Services/InspectionPilotProbeDataSource.cs</c> and pulls
/// from <c>InspectionDbContext</c> — kept out of the platform layer to
/// avoid a Tenancy.Database → Inspection.Database project reference (the
/// platform layer does not know about the inspection module by design).
/// </summary>
/// <remarks>
/// All methods are tenant-scoped reads. The portal-side implementation
/// hits <c>InspectionDbContext</c> under whatever <c>ITenantContext</c>
/// is active when the dashboard refresh is invoked — so RLS does the
/// scoping work and no <c>SetSystemContext</c> flip is needed (the brief
/// is explicit: per-tenant fan-out, not system-wide).
/// </remarks>
public interface IInspectionPilotProbeDataSource
{
    /// <summary>
    /// Returns <c>true</c> if at least one <c>InspectionCase</c> in the
    /// supplied tenant has been decisioned (<c>Verdict</c> exists) AND
    /// <c>IsSynthetic = false</c>. Used by <c>gate.analyst.decisioned_real_case</c>.
    /// </summary>
    Task<bool> HasDecisionedRealCaseAsync(long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if at least one <c>OutboundSubmission</c> in
    /// the supplied tenant has <c>Status = "accepted"</c> AND
    /// <c>LastAttemptAt</c> not null. Used by
    /// <c>gate.external_system.roundtrip</c> — a single accepted
    /// round-trip suffices (vendor-neutral; ICUMS / CMR / BOE all hit
    /// this gate via the same status flip in the existing
    /// <c>OutboundSubmissionDispatchWorker</c>).
    /// </summary>
    Task<bool> HasSuccessfulOutboundSubmissionAsync(long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the <c>CaseId</c> of the most recently decisioned
    /// non-synthetic case for the tenant, or null if none exists. Lets
    /// the readiness probe surface "your most recent real verdict was
    /// case X" hint text on the dashboard.
    /// </summary>
    Task<Guid?> LatestDecisionedRealCaseIdAsync(long tenantId, CancellationToken ct = default);
}
