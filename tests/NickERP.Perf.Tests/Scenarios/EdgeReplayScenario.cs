using Microsoft.Extensions.Configuration;
using NBomber.Contracts;

namespace NickERP.Perf.Tests.Scenarios;

/// <summary>
/// Sprint 55 — Phase B placeholder. Real implementation lands in the
/// next commit. Currently always returns null so the dispatcher logs
/// "skipping" and exits 0; this keeps the harness shippable while
/// Phase A is the only live scenario.
/// </summary>
public static class EdgeReplayScenario
{
    public const string ScenarioName = "edge-replay";

    /// <summary>Endpoint under test, per docs/perf/test-plan.md §2.1 EP-005.</summary>
    public const string EndpointPath = "/api/edge/replay";

    /// <summary>Acceptance-gate latency in ms at 1x pilot peak.</summary>
    public const int Pilot1xP99AcceptanceMs = 500;

    /// <summary>BLOCK-pilot latency in ms at 1x pilot peak.</summary>
    public const int Pilot1xP99BlockMs = 1500;

    /// <summary>
    /// Sprint 30 SEC-EDGE-7 — env var carrying the per-edge HMAC API
    /// key. When unset the scenario skips with a logged note.
    /// </summary>
    public const string EdgeHmacKeyEnvVar = "NICKERP_PERF_EDGE_HMAC_KEY";

    public static ScenarioProps? Build(IConfiguration config, LoadProfile profile)
    {
        Console.WriteLine("edge-replay: Phase B not yet shipped — skipping.");
        return null;
    }

    public static ScenarioProps? BuildBacklog(IConfiguration config)
    {
        Console.WriteLine("edge-replay-backlog: Phase B not yet shipped — skipping.");
        return null;
    }

    public static int CheckAcceptanceGate(double p99Ms, LoadProfile profile, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        log($"edge-replay: gate-check stub — p99={p99Ms:F0}ms profile={profile}");
        return 0;
    }
}
