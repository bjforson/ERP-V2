using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Tenancy.Pilot;

namespace NickERP.Platform.Tenancy.Database.Pilot;

/// <summary>
/// Sprint 43 — active multi-tenant invariant probe. Backs the marquee
/// gate <see cref="PilotReadinessGate.MultiTenantInvariants"/> in
/// <c>PilotReadinessService</c>. Three sub-checks; all three must pass
/// for the gate to pass.
/// </summary>
/// <remarks>
/// <para>
/// This is the only probe in Sprint 43 that is allowed to flip
/// <c>SetSystemContext</c> — the system-context register integrity
/// check enumerates every caller and the cross-tenant export refusal
/// check has to escape tenant scope to attempt the cross-tenant read.
/// Both flips are bounded to this probe's execution window and
/// reverted before the probe returns.
/// </para>
/// <para>
/// Phase B is the impl skeleton — Phase C fills in the actual checks.
/// Until Phase C lands the probe returns <c>Fail</c> with a "not yet
/// implemented" note. PilotReadinessService surfaces that as the gate's
/// <c>Note</c> so the dashboard never crashes during the gap.
/// </para>
/// </remarks>
public class MultiTenantInvariantProbe
{
    private readonly TenancyDbContext _tenancyDb;
    private readonly TimeProvider _clock;
    private readonly ILogger<MultiTenantInvariantProbe> _logger;

    public MultiTenantInvariantProbe(
        TenancyDbContext tenancyDb,
        TimeProvider clock,
        ILogger<MultiTenantInvariantProbe> logger)
    {
        _tenancyDb = tenancyDb ?? throw new ArgumentNullException(nameof(tenancyDb));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Run all three sub-checks against the supplied tenant. Phase B
    /// stub — returns Fail with a "phase C deferred" note. Phase C
    /// supersedes this body with the real checks.
    /// </summary>
    public virtual Task<MultiTenantInvariantProbeResult> RunAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        var note = "Phase B stub — actual probe lands in Phase C of Sprint 43.";
        var result = new MultiTenantInvariantProbeResult(
            OverallPass: false,
            ObservedAt: _clock.GetUtcNow(),
            ProofEventId: null,
            RlsReadIsolation: new MultiTenantInvariantSubCheck(false, note),
            SystemContextRegister: new MultiTenantInvariantSubCheck(false, note),
            CrossTenantExportGate: new MultiTenantInvariantSubCheck(false, note));
        return Task.FromResult(result);
    }
}

/// <summary>Aggregate result of a single probe run.</summary>
public sealed record MultiTenantInvariantProbeResult(
    bool OverallPass,
    DateTimeOffset ObservedAt,
    Guid? ProofEventId,
    MultiTenantInvariantSubCheck RlsReadIsolation,
    MultiTenantInvariantSubCheck SystemContextRegister,
    MultiTenantInvariantSubCheck CrossTenantExportGate);

/// <summary>Outcome of one sub-check.</summary>
public sealed record MultiTenantInvariantSubCheck(bool Pass, string Reason);
