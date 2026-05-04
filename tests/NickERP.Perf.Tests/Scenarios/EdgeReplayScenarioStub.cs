namespace NickERP.Perf.Tests.Scenarios;

/// <summary>
/// STUB — edge-replay scenario for the edge node's central-write hot path
/// (<c>POST /api/edge/replay</c> per test-plan §2.1 EP-005).
/// </summary>
/// <remarks>
/// <para>
/// Not yet executable. Phase V execution requires:
/// </para>
/// <list type="number">
///   <item>A test edge node identity row in <c>audit.edge_node_authorizations</c></item>
///   <item>A per-edge HMAC API key (issued via the admin flow, captured in test config)</item>
///   <item>Buffer fixture generation: 1-20 events per batch, mixed types (audit.event.replay + inspection.scan.captured + inspection.scanner.status.changed)</item>
///   <item>Backlog fixtures: 24h of buffered events to test the SEC-EDGE-7 rate-limit verification scenario</item>
/// </list>
/// <para>
/// Profile RPS (per test-plan §3.1 + §5):
/// </para>
/// <list type="bullet">
///   <item>1x pilot — 0.5 RPS at peak (per node × 3 nodes; 30s flush cadence with 5 events mean), p99 acceptance gate 500 ms (BLOCK at 1500 ms)</item>
///   <item>5x — concurrent flushes from 4 nodes, mixed event-type batches</item>
///   <item>Backlog scenario — long-offline edge reconnects; verify rate-limit prevents central DB DOS</item>
/// </list>
/// <para>
/// Fan-out check: per Sprint 17, the replay endpoint is a per-EventTypeHint
/// dispatcher. The perf scenario must exercise all 3 supported hints to
/// catch latency regressions in any single dispatch path.
/// </para>
/// </remarks>
public static class EdgeReplayScenarioStub
{
    public const string ScenarioName = "edge-replay";

    /// <summary>Endpoint under test, per docs/perf/test-plan.md §2.1.</summary>
    public const string EndpointPath = "/api/edge/replay";

    /// <summary>Acceptance-gate latency in ms at 1x pilot peak.</summary>
    public const int Pilot1xP99AcceptanceMs = 500;

    /// <summary>BLOCK-pilot latency in ms at 1x pilot peak.</summary>
    public const int Pilot1xP99BlockMs = 1500;

    /// <summary>Supported event-type hints (per Sprint 17 EdgeEventTypes static class).</summary>
    public static readonly string[] SupportedHints = new[]
    {
        "audit.event.replay",
        "inspection.scan.captured",
        "inspection.scanner.status.changed",
    };
}
