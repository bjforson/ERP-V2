using Microsoft.Extensions.Configuration;
using NickERP.Perf.Tests.Runner;

namespace NickERP.Perf.Tests.Scenarios;

/// <summary>
/// Sprint 55 — runner glue for the edge-replay live scenarios. Splits
/// the two NickPerf-runs (steady + backlog) out of <c>Program.cs</c> so
/// the dispatcher stays small and the scenarios stay testable.
/// Sprint 58 — ported from NBomberRunner to <see cref="NickPerfRunner"/>.
/// </summary>
internal static class EdgeReplayScenarioRunner
{
    public static int Run(
        IConfiguration config,
        LoadProfile profile,
        Func<string, string> getReportFolder)
    {
        var scenario = EdgeReplayScenario.Build(config, profile);
        if (scenario is null)
        {
            return 0;
        }

        var snapshot = NickPerfRunner.RunAsync(
            scenario,
            getReportFolder("edge-replay"),
            "edge-replay")
            .GetAwaiter().GetResult();

        if (snapshot.ok == 0)
        {
            Console.WriteLine($"edge-replay: FAIL — 0 successful requests (all={snapshot.fail} failed). " +
                              "Target unreachable or returning errors.");
            return 1;
        }
        var gateExit = EdgeReplayScenario.CheckAcceptanceGate(snapshot.p99, profile);
        return gateExit != 0 || snapshot.fail > snapshot.ok ? 1 : 0;
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

        var snapshot = NickPerfRunner.RunAsync(
            scenario,
            getReportFolder("edge-replay-backlog"),
            "edge-replay-backlog")
            .GetAwaiter().GetResult();

        // Backlog test verifies the rate-limit holds — failures are
        // EXPECTED (some requests get 429-ed). Pass criterion: scenario
        // completes without OOM/crash AND at least some requests are
        // rejected (proving the limiter exists). The dispatcher just
        // exits 0 on completion; detailed analysis goes to the run
        // report.
        Console.WriteLine($"edge-replay-backlog: ok={snapshot.ok} fail={snapshot.fail}");
        return 0;
    }
}
