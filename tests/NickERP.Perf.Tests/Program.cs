using Microsoft.Extensions.Configuration;
using NBomber.CSharp;
using NickERP.Perf.Tests;
using NickERP.Perf.Tests.Scenarios;

// Sprint 55 — Phase V perf-test harness entry point. Replaces the
// Sprint 30 stubs with live-running scenarios.
//
// Usage:
//   dotnet run --project tests/NickERP.Perf.Tests -- <scenario> [--profile 1x|5x|10x]
//
// Available scenarios:
//   * health              smoke against /healthz/live
//   * case-create         POST /api/inspection/cases (replaces Sprint 30 stub)
//   * edge-replay         POST /api/edge/replay (replaces Sprint 30 stub)
//   * edge-replay-backlog 24h backlog reconnect — verifies SEC-EDGE-7 rate limit
//
// Skip-on-misconfigured behaviour:
//   * Each live scenario inspects required config (target URL, auth token,
//     edge HMAC key) and gracefully exits 0 if missing. This keeps CI green
//     when the perf rig has no target wired.
//
// Reports land in tests/NickERP.Perf.Tests/bin/<config>/<tfm>/reports/{date}/{scenario}/

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "NICKERP_PERF_")
    .Build();

var scenarioName = args.Length > 0 ? args[0] : "health";
var profile = ParseProfile(args);

Console.WriteLine($"NickERP perf harness | scenario={scenarioName} profile={profile} target={config["TargetBaseUrl"]}");

try
{
    return scenarioName switch
    {
        "health" => RunHealthScenario(config, profile),
        "case-create" => RunCaseCreateScenario(config, profile),
        "edge-replay" => RunEdgeReplayScenario(config, profile),
        "edge-replay-backlog" => RunEdgeReplayBacklogScenario(config),
        _ => UnknownScenario(scenarioName),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 2;
}

static LoadProfile ParseProfile(string[] argv)
{
    for (var i = 0; i < argv.Length - 1; i++)
    {
        if (argv[i] == "--profile")
        {
            return argv[i + 1] switch
            {
                "1x" => LoadProfile.Pilot1x,
                "5x" => LoadProfile.Tema5x,
                "10x" => LoadProfile.Stress10x,
                _ => LoadProfile.Pilot1x,
            };
        }
    }
    return LoadProfile.Pilot1x;
}

static int RunHealthScenario(IConfiguration config, LoadProfile profile)
{
    var scenario = HealthEndpointScenario.Build(config, profile);

    var stats = NBomberRunner
        .RegisterScenarios(scenario)
        .WithTestSuite("nickerp-perf")
        .WithTestName("health")
        .WithReportFolder(GetReportFolder("health"))
        .Run();

    return stats.AllFailCount == 0 ? 0 : 1;
}

static int RunCaseCreateScenario(IConfiguration config, LoadProfile profile)
{
    var scenario = CaseCreateScenario.Build(config, profile);
    if (scenario is null)
    {
        // Skip-on-misconfigured — already logged by Build.
        return 0;
    }

    var stats = NBomberRunner
        .RegisterScenarios(scenario)
        .WithTestSuite("nickerp-perf")
        .WithTestName("case-create")
        .WithReportFolder(GetReportFolder("case-create"))
        .Run();

    if (stats.AllOkCount == 0)
    {
        Console.WriteLine($"case-create: FAIL — 0 successful requests (all={stats.AllFailCount} failed). " +
                          "Target unreachable or returning errors. p99 acceptance gate cannot be evaluated.");
        return 1;
    }
    var p99 = ExtractP99Ms(stats, CaseCreateScenario.ScenarioName);
    var gateExit = CaseCreateScenario.CheckAcceptanceGate(p99, profile);
    return gateExit != 0 || stats.AllFailCount > stats.AllOkCount ? 1 : 0;
}

static int RunEdgeReplayScenario(IConfiguration config, LoadProfile profile)
{
    // Phase B — implementation lands in EdgeReplayScenario.cs.
    // The dispatcher routes here; the scenario's own ShouldSkip logic
    // gates at-run.
    return EdgeReplayScenarioRunner.Run(config, profile, GetReportFolder, ExtractP99Ms);
}

static int RunEdgeReplayBacklogScenario(IConfiguration config)
{
    return EdgeReplayScenarioRunner.RunBacklog(config, GetReportFolder);
}

static int UnknownScenario(string name)
{
    Console.Error.WriteLine(
        $"Unknown scenario '{name}'. Available: health, case-create, edge-replay, edge-replay-backlog");
    return 1;
}

static string GetReportFolder(string scenarioName)
{
    var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
    var path = Path.Combine(AppContext.BaseDirectory, "reports", date, scenarioName);
    Directory.CreateDirectory(path);
    return path;
}

// Pull the p99 from NBomber's stats. The ScenarioStats type exposes
// Ok.Latency.Percent99 in milliseconds. When the scenario produced no
// successful requests we report +inf so the gate fails loudly.
static double ExtractP99Ms(NBomber.Contracts.Stats.NodeStats stats, string scenarioName)
{
    foreach (var s in stats.ScenarioStats)
    {
        if (string.Equals(s.ScenarioName, scenarioName, StringComparison.Ordinal))
        {
            // Latency is reported in ms.
            return s.Ok.Latency.Percent99;
        }
    }
    return double.PositiveInfinity;
}
