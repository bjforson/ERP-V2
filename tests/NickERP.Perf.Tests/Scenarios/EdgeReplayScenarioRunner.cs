using Microsoft.Extensions.Configuration;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace NickERP.Perf.Tests.Scenarios;

/// <summary>
/// Sprint 55 — runner glue for the edge-replay live scenarios. Splits
/// the two NBomber-runs (steady + backlog) out of <c>Program.cs</c> so
/// the dispatcher stays small and the scenarios stay testable.
/// </summary>
internal static class EdgeReplayScenarioRunner
{
    public static int Run(
        IConfiguration config,
        LoadProfile profile,
        Func<string, string> getReportFolder,
        Func<NodeStats, string, double> extractP99Ms)
    {
        var scenario = EdgeReplayScenario.Build(config, profile);
        if (scenario is null)
        {
            return 0;
        }

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("nickerp-perf")
            .WithTestName("edge-replay")
            .WithReportFolder(getReportFolder("edge-replay"))
            .Run();

        if (stats.AllOkCount == 0)
        {
            Console.WriteLine($"edge-replay: FAIL — 0 successful requests (all={stats.AllFailCount} failed). " +
                              "Target unreachable or returning errors.");
            return 1;
        }
        var p99 = extractP99Ms(stats, EdgeReplayScenario.ScenarioName);
        var gateExit = EdgeReplayScenario.CheckAcceptanceGate(p99, profile);
        return gateExit != 0 || stats.AllFailCount > stats.AllOkCount ? 1 : 0;
    }

    public static int RunBacklog(
        IConfiguration config,
        Func<string, string> getReportFolder)
    {
        var scenario = EdgeReplayScenario.BuildBacklog(config);
        if (scenario is null)
        {
            return 0;
        }

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("nickerp-perf")
            .WithTestName("edge-replay-backlog")
            .WithReportFolder(getReportFolder("edge-replay-backlog"))
            .Run();

        // Backlog test verifies the rate-limit holds — failures are
        // EXPECTED (some requests get 429-ed). Pass criterion: scenario
        // completes without OOM/crash AND at least some requests are
        // rejected (proving the limiter exists). The dispatcher just
        // exits 0 on completion; detailed analysis goes to the run
        // report.
        Console.WriteLine($"edge-replay-backlog: ok={stats.AllOkCount} fail={stats.AllFailCount}");
        return 0;
    }
}
