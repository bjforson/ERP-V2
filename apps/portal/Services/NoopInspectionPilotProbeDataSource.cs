using NickERP.Platform.Tenancy.Pilot;

namespace NickERP.Portal.Services;

/// <summary>
/// Sprint 43 — fallback <see cref="IInspectionPilotProbeDataSource"/>
/// for portal hosts that do NOT have <c>ConnectionStrings:Inspection</c>
/// configured. Every method returns the "no observations" answer, so
/// the readiness dashboard's analyst + external-system gates surface
/// as <c>NotYetObserved</c> with the standard "what's needed" hint.
/// </summary>
/// <remarks>
/// Mirrors the existing pattern <see cref="InspectionFeatureFlag"/>
/// uses to gracefully run portal-only deployments where the inspection
/// module is not deployed alongside the portal binary.
/// </remarks>
public sealed class NoopInspectionPilotProbeDataSource : IInspectionPilotProbeDataSource
{
    public Task<bool> HasDecisionedRealCaseAsync(long tenantId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> HasSuccessfulOutboundSubmissionAsync(long tenantId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<Guid?> LatestDecisionedRealCaseIdAsync(long tenantId, CancellationToken ct = default)
        => Task.FromResult<Guid?>(null);
}
