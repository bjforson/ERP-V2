using NickERP.Perf.Tests.Runner;

namespace NickERP.Perf.Tests.Scenarios.Helpers;

/// <summary>
/// Sprint 58 — builds <see cref="NickPerfLoadProfile"/>s for each load
/// profile + scenario. Replaces the Sprint 55 NBomber-shape factory with
/// the homegrown rate-based shape; the RPS targets (per
/// <c>docs/perf/test-plan.md</c> §3.1) are unchanged.
/// </summary>
public static class LoadSimulationFactory
{
    // 5 s warmup window applied to every measured scenario — matches
    // appsettings.json ScenarioDefaults.WarmupSeconds. Keeps first-request
    // JIT / DNS / HttpClient cold-start cost off the measured p99.
    // Backlog scenario opts out (it's informative, not gated).
    private static readonly TimeSpan StandardWarmup = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Case-create profile per test-plan §3.1 EP-001.
    /// 1x = 0.35 RPS, 5x = 1.75 RPS, 10x = 3.5 RPS.
    /// We keep <c>Rate=N, Interval=1min</c> because finer fractional
    /// targets land cleanly that way (e.g. 21/min → 0.35 RPS).
    /// </summary>
    public static NickPerfLoadProfile BuildCaseCreate(LoadProfile profile, TimeSpan during)
    {
        return profile switch
        {
            // 0.35 RPS = 21 / minute. Inject 21 over 60s.
            LoadProfile.Pilot1x => new NickPerfLoadProfile { Rate = 21, Interval = TimeSpan.FromMinutes(1), Duration = during, Warmup = StandardWarmup },
            // 1.75 RPS = 105 / minute.
            LoadProfile.Tema5x => new NickPerfLoadProfile { Rate = 105, Interval = TimeSpan.FromMinutes(1), Duration = during, Warmup = StandardWarmup },
            // 3.5 RPS = 210 / minute.
            LoadProfile.Stress10x => new NickPerfLoadProfile { Rate = 210, Interval = TimeSpan.FromMinutes(1), Duration = during, Warmup = StandardWarmup },
            _ => new NickPerfLoadProfile { Rate = 21, Interval = TimeSpan.FromMinutes(1), Duration = during, Warmup = StandardWarmup }
        };
    }

    /// <summary>
    /// Edge-replay profile per test-plan §3.1 EP-005.
    /// 1x = 0.5 RPS, 5x = 2.5 RPS, 10x = 5 RPS (informative).
    /// </summary>
    public static NickPerfLoadProfile BuildEdgeReplay(LoadProfile profile, TimeSpan during)
    {
        return profile switch
        {
            // 0.5 RPS = 30 / minute.
            LoadProfile.Pilot1x => new NickPerfLoadProfile { Rate = 30, Interval = TimeSpan.FromMinutes(1), Duration = during, Warmup = StandardWarmup },
            // 2.5 RPS = 150 / minute.
            LoadProfile.Tema5x => new NickPerfLoadProfile { Rate = 150, Interval = TimeSpan.FromMinutes(1), Duration = during, Warmup = StandardWarmup },
            // 5.0 RPS = 300 / minute.
            LoadProfile.Stress10x => new NickPerfLoadProfile { Rate = 300, Interval = TimeSpan.FromMinutes(1), Duration = during, Warmup = StandardWarmup },
            _ => new NickPerfLoadProfile { Rate = 30, Interval = TimeSpan.FromMinutes(1), Duration = during, Warmup = StandardWarmup }
        };
    }

    /// <summary>
    /// Edge-backlog profile per test-plan §5 — a long-offline edge
    /// reconnects and dumps a 24h backlog. Submits <paramref name="batches"/>
    /// over <paramref name="during"/> to verify the central rate-limit
    /// holds (per Sprint 30 SEC-EDGE-7). Default 600 batches in 60s = 10
    /// RPS — high enough to trigger the rate-limit without blowing up
    /// the test rig.
    /// </summary>
    public static NickPerfLoadProfile BuildEdgeBacklog(TimeSpan during, int batches = 600)
    {
        return new NickPerfLoadProfile
        {
            Rate = batches,
            Interval = during,
            Duration = during
        };
    }
}
