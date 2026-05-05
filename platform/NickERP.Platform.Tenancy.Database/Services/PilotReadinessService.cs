using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy.Database.Pilot;
using NickERP.Platform.Tenancy.Pilot;

namespace NickERP.Platform.Tenancy.Database.Services;

/// <summary>
/// Sprint 43 — default <see cref="IPilotReadinessService"/>. Runs all 5
/// gates against the supplied tenant on every refresh, persists one
/// append-only snapshot row per gate to
/// <c>tenancy.pilot_readiness_snapshots</c>, and returns the report for
/// the dashboard at <c>/admin/pilot-readiness</c>.
/// </summary>
/// <remarks>
/// <para>
/// All probes operate under the calling thread's existing
/// <see cref="ITenantContext"/> — no <c>SetSystemContext</c> flips here.
/// The dashboard is admin-only and is invoked under a real admin user
/// who already has tenant scope; the audit-event reads narrow by
/// <c>WHERE TenantId == tenantId</c> in LINQ so RLS does the scoping
/// work without a context change.
/// </para>
/// <para>
/// Gate 5 (multi-tenant invariants) delegates to
/// <see cref="MultiTenantInvariantProbe"/>. As of Phase C the probe
/// runs three real sub-checks (RLS read isolation, system-context
/// register integrity, cross-tenant export gate refusal) and emits
/// a <c>nickerp.tenancy.invariant_probe_run</c> audit event per
/// run.
/// </para>
/// <para>
/// Probe failures never throw out of this service. Each gate is wrapped
/// in a try/catch that converts exceptions into
/// <see cref="PilotReadinessState.Fail"/> with the failure message in
/// the snapshot's <c>Note</c>. The dashboard renders Fail like any
/// other state.
/// </para>
/// </remarks>
public sealed class PilotReadinessService : IPilotReadinessService
{
    private readonly TenancyDbContext _tenancyDb;
    private readonly AuditDbContext _auditDb;
    private readonly IInspectionPilotProbeDataSource _inspection;
    private readonly MultiTenantInvariantProbe _invariantProbe;
    private readonly TimeProvider _clock;
    private readonly ILogger<PilotReadinessService> _logger;

    public PilotReadinessService(
        TenancyDbContext tenancyDb,
        AuditDbContext auditDb,
        IInspectionPilotProbeDataSource inspection,
        MultiTenantInvariantProbe invariantProbe,
        TimeProvider clock,
        ILogger<PilotReadinessService> logger)
    {
        _tenancyDb = tenancyDb ?? throw new ArgumentNullException(nameof(tenancyDb));
        _auditDb = auditDb ?? throw new ArgumentNullException(nameof(auditDb));
        _inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));
        _invariantProbe = invariantProbe ?? throw new ArgumentNullException(nameof(invariantProbe));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<PilotReadinessReport> GetReadinessAsync(long tenantId, CancellationToken ct = default)
    {
        if (tenantId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "Tenant id must be positive.");
        }

        var observedAt = _clock.GetUtcNow();
        var results = new List<PilotReadinessGateResult>(PilotReadinessGate.All.Count);

        results.Add(await SafeProbeAsync(
            PilotReadinessGate.ScannerAdapter,
            ProbeScannerAdapterAsync,
            tenantId, observedAt, ct));

        results.Add(await SafeProbeAsync(
            PilotReadinessGate.EdgeRoundtrip,
            ProbeEdgeRoundtripAsync,
            tenantId, observedAt, ct));

        results.Add(await SafeProbeAsync(
            PilotReadinessGate.AnalystDecisionedRealCase,
            ProbeAnalystDecisionedRealCaseAsync,
            tenantId, observedAt, ct));

        results.Add(await SafeProbeAsync(
            PilotReadinessGate.ExternalSystemRoundtrip,
            ProbeExternalSystemRoundtripAsync,
            tenantId, observedAt, ct));

        results.Add(await SafeProbeAsync(
            PilotReadinessGate.MultiTenantInvariants,
            ProbeMultiTenantInvariantsAsync,
            tenantId, observedAt, ct));

        // Persist one append-only row per gate. INSERT-only by design —
        // the table grant in Add_PilotReadinessSnapshots intentionally
        // omits UPDATE so a Pass→Fail→Pass transition leaves three
        // distinct rows on disk for the operator to triage.
        foreach (var r in results)
        {
            _tenancyDb.PilotReadinessSnapshots.Add(new PilotReadinessSnapshot
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                GateId = r.GateId,
                State = r.State,
                ObservedAt = r.ObservedAt,
                ProofEventId = r.ProofEventId,
                Note = r.Note,
            });
        }
        await _tenancyDb.SaveChangesAsync(ct);

        return new PilotReadinessReport(tenantId, observedAt, results);
    }

    // ---- gate probes ----------------------------------------------------

    /// <summary>
    /// gate.scanner.adapter — at least one
    /// <c>nickerp.inspection.scan_recorded</c> event for the tenant.
    /// Vendor-neutral by construction (the same audit event is emitted
    /// regardless of FS6000 / ASE / mock — see CaseWorkflowService).
    /// </summary>
    private async Task<(PilotReadinessState state, Guid? proofEventId, string? note)> ProbeScannerAdapterAsync(
        long tenantId, CancellationToken ct)
    {
        var row = await _auditDb.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EventType == "nickerp.inspection.scan_recorded")
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.EventId, e.OccurredAt })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return (PilotReadinessState.NotYetObserved, null,
                "No scan_recorded audit event observed yet — register a scanner adapter and run at least one scan to satisfy this gate.");
        }
        return (PilotReadinessState.Pass, row.EventId, $"First observed at {row.OccurredAt:O}.");
    }

    /// <summary>
    /// gate.edge.roundtrip — at least one
    /// <c>inspection.scan.captured</c> event for the tenant. The
    /// EdgeReplayEndpoint writes this event when an edge node submits a
    /// replay payload (see EdgeReplayEndpoint.AugmentPayload). One
    /// successful replay is sufficient evidence of edge → server →
    /// audit round-trip.
    /// </summary>
    private async Task<(PilotReadinessState state, Guid? proofEventId, string? note)> ProbeEdgeRoundtripAsync(
        long tenantId, CancellationToken ct)
    {
        var row = await _auditDb.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EventType == "inspection.scan.captured")
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { e.EventId, e.OccurredAt })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return (PilotReadinessState.NotYetObserved, null,
                "No edge replay observed yet — set up at least one edge node, capture a scan offline, and trigger a successful replay to satisfy this gate.");
        }
        return (PilotReadinessState.Pass, row.EventId, $"First observed at {row.OccurredAt:O}.");
    }

    /// <summary>
    /// gate.analyst.decisioned_real_case — at least one
    /// <c>nickerp.inspection.verdict_set</c> event for the tenant whose
    /// underlying <c>InspectionCase.IsSynthetic</c> is false. The
    /// inspection-side <see cref="IInspectionPilotProbeDataSource"/>
    /// answers the IsSynthetic question because Tenancy.Database does
    /// not (and should not) reference Inspection.Database.
    /// </summary>
    private async Task<(PilotReadinessState state, Guid? proofEventId, string? note)> ProbeAnalystDecisionedRealCaseAsync(
        long tenantId, CancellationToken ct)
    {
        var hasReal = await _inspection.HasDecisionedRealCaseAsync(tenantId, ct);
        if (!hasReal)
        {
            return (PilotReadinessState.NotYetObserved, null,
                "No analyst has decisioned a non-synthetic case yet — get an analyst to verdict at least one production case (IsSynthetic = false) to satisfy this gate.");
        }

        var caseId = await _inspection.LatestDecisionedRealCaseIdAsync(tenantId, ct);

        // Find the matching verdict_set audit row to use as proof. We
        // narrow by EntityId = caseId.ToString() because the inspection
        // emitter writes the case id as the EntityId on verdict_set.
        Guid? proofEventId = null;
        DateTimeOffset proofAt = DateTimeOffset.MinValue;
        if (caseId is { } cid)
        {
            var verdictRow = await _auditDb.Events
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId
                    && e.EventType == "nickerp.inspection.verdict_set"
                    && e.EntityId == cid.ToString())
                .OrderByDescending(e => e.OccurredAt)
                .Select(e => new { e.EventId, e.OccurredAt })
                .FirstOrDefaultAsync(ct);
            if (verdictRow is not null)
            {
                proofEventId = verdictRow.EventId;
                proofAt = verdictRow.OccurredAt;
            }
        }

        var note = proofAt > DateTimeOffset.MinValue
            ? $"Latest non-synthetic verdict: case {caseId} at {proofAt:O}."
            : null;
        return (PilotReadinessState.Pass, proofEventId, note);
    }

    /// <summary>
    /// gate.external_system.roundtrip — at least one
    /// <c>OutboundSubmission</c> in <c>Status = "accepted"</c> with
    /// <c>LastAttemptAt</c> not null. Vendor-neutral; the same code
    /// path serves ICUMS / CMR / BOE / post-hoc adapters.
    /// </summary>
    private async Task<(PilotReadinessState state, Guid? proofEventId, string? note)> ProbeExternalSystemRoundtripAsync(
        long tenantId, CancellationToken ct)
    {
        var has = await _inspection.HasSuccessfulOutboundSubmissionAsync(tenantId, ct);
        if (!has)
        {
            return (PilotReadinessState.NotYetObserved, null,
                "No accepted outbound submission yet — get at least one verdict dispatched to an external system (status = 'accepted') to satisfy this gate.");
        }
        return (PilotReadinessState.Pass, null, null);
    }

    /// <summary>
    /// gate.multi_tenant.invariants — delegates to the active probe in
    /// <see cref="MultiTenantInvariantProbe"/>. The probe's three
    /// sub-checks are summarised in the resulting <c>Note</c> so the
    /// dashboard can render the breakdown.
    /// </summary>
    private async Task<(PilotReadinessState state, Guid? proofEventId, string? note)> ProbeMultiTenantInvariantsAsync(
        long tenantId, CancellationToken ct)
    {
        var probe = await _invariantProbe.RunAsync(tenantId, ct);
        var state = probe.OverallPass
            ? PilotReadinessState.Pass
            : PilotReadinessState.Fail;

        var rlsLine = probe.RlsReadIsolation.Pass ? "rls_read_isolation:pass" : $"rls_read_isolation:fail({probe.RlsReadIsolation.Reason})";
        var registerLine = probe.SystemContextRegister.Pass ? "system_context_register:pass" : $"system_context_register:fail({probe.SystemContextRegister.Reason})";
        var exportLine = probe.CrossTenantExportGate.Pass ? "cross_tenant_export_gate:pass" : $"cross_tenant_export_gate:fail({probe.CrossTenantExportGate.Reason})";
        var note = $"{rlsLine}; {registerLine}; {exportLine}";

        return (state, probe.ProofEventId, note);
    }

    // ---- safe-probe wrapper --------------------------------------------

    private async Task<PilotReadinessGateResult> SafeProbeAsync(
        string gateId,
        Func<long, CancellationToken, Task<(PilotReadinessState state, Guid? proofEventId, string? note)>> probe,
        long tenantId,
        DateTimeOffset observedAt,
        CancellationToken ct)
    {
        try
        {
            var (state, proofEventId, note) = await probe(tenantId, ct);
            return new PilotReadinessGateResult(gateId, state, observedAt, proofEventId, note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "PilotReadinessService probe for {GateId} on tenant {TenantId} failed; recording Fail state.",
                gateId, tenantId);
            // Bound the note to fit the column's max length even if the
            // exception message is huge.
            var note = ex.Message.Length > 800 ? ex.Message[..800] : ex.Message;
            return new PilotReadinessGateResult(gateId, PilotReadinessState.Fail, observedAt, null, note);
        }
    }
}
