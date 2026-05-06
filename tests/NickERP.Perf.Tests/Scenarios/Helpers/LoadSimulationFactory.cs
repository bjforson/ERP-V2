using NBomber.Contracts;
using NBomber.CSharp;

namespace NickERP.Perf.Tests.Scenarios.Helpers;

/// <summary>
/// Sprint 55 — builds NBomber <see cref="LoadSimulation"/>s for each
/// load profile + scenario. Centralised so the per-scenario plumbing
/// stays readable + the RPS targets stay easy to audit against
/// <c>docs/perf/test-plan.md</c> §3.1.
/// </summary>
public static class LoadSimulationFactory
{
    /// <summary>
    /// Case-create profile per test-plan §3.1 EP-001.
    /// 1x = 0.35 RPS, 5x = 1.75 RPS, 10x = 3.5 RPS.
    /// NBomber's <c>Inject</c> with <c>rate=1, interval=Xs</c> gives
    /// 1/X RPS; we use larger rate values so finer fractional targets
    /// are achievable without sub-second intervals.
    /// </summary>
    public static LoadSimulation BuildCaseCreate(LoadProfile profile, TimeSpan during)
    {
        return profile switch
        {
            // 0.35 RPS = 21 / minute. Inject 21 over 60s.
            LoadProfile.Pilot1x => Simulation.Inject(rate: 21, interval: TimeSpan.FromMinutes(1), during: during),
            // 1.75 RPS = 105 / minute.
            LoadProfile.Tema5x => Simulation.Inject(rate: 105, interval: TimeSpan.FromMinutes(1), during: during),
            // 3.5 RPS = 210 / minute.
            LoadProfile.Stress10x => Simulation.Inject(rate: 210, interval: TimeSpan.FromMinutes(1), during: during),
            _ => Simulation.Inject(rate: 21, interval: TimeSpan.FromMinutes(1), during: during)
        };
    }

    /// <summary>
    /// Edge-replay profile per test-plan §3.1 EP-005.
    /// 1x = 0.5 RPS, 5x = 2.5 RPS, 10x = 5 RPS (informative).
    /// </summary>
    public static LoadSimulation BuildEdgeReplay(LoadProfile profile, TimeSpan during)
    {
        return profile switch
        {
            // 0.5 RPS = 30 / minute.
            LoadProfile.Pilot1x => Simulation.Inject(rate: 30, interval: TimeSpan.FromMinutes(1), during: during),
            // 2.5 RPS = 150 / minute.
            LoadProfile.Tema5x => Simulation.Inject(rate: 150, interval: TimeSpan.FromMinutes(1), during: during),
            // 5.0 RPS = 300 / minute.
            LoadProfile.Stress10x => Simulation.Inject(rate: 300, interval: TimeSpan.FromMinutes(1), during: during),
            _ => Simulation.Inject(rate: 30, interval: TimeSpan.FromMinutes(1), during: during)
        };
    }

    /// <summary>
    /// Edge-backlog profile per test-plan §5 — a long-offline edge
    /// reconnects and dumps a 24h backlog. The scenario submits batches
    /// as fast as the central rate-limit will allow; we configure the
    /// scenario at a higher target than 5x to verify the rate-limit
    /// holds (per Sprint 30 SEC-EDGE-7).
    /// </summary>
    public static LoadSimulation BuildEdgeBacklog(TimeSpan during, int batches = 600)
    {
        // Spread the batches over the duration evenly. Default 600
        // batches in 60s = 10 RPS; high enough to trigger the rate-limit
        // without blowing up the test rig.
        return Simulation.Inject(
            rate: batches,
            interval: during,
            during: during);
    }
}
