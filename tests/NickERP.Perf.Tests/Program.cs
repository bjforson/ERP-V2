using Microsoft.Extensions.Configuration;
using NickERP.Perf.Tests;
using NickERP.Perf.Tests.Runner;
using NickERP.Perf.Tests.Scenarios;

// Sprint 55 — Phase V perf-test harness entry point. Replaces the
// Sprint 30 stubs with live-running scenarios.
//
// Sprint 58 — replaced NBomber 6.1.0 (commercial subscription) with the
// homegrown NickPerf in-tree runner under Runner/. Behaviour unchanged;
// scenarios still skip-on-misconfigured, still write per-scenario
// markdown reports, still expose the same exit-code contract. Adapter
// types (NickPerfScenario, NickPerfRunner, NickPerfStats, NickPerfReport)
// are vendor-neutral and have NO non-allowlisted dependencies.
//
// Usage:
//   dotnet run --project tests/NickERP.Perf.Tests -- <scenario> [--profile 1x|5x|10x]
//
// Available scenarios:
//   * health              smoke against /healthz/live
//   * case-create         POST /api/inspection/cases (replaces Sprint 30 stub)
//   * edge-replay         POST /api/edge/replay (replaces Sprint 30 stub)
//   * edge-replay-backlog 24h backlog reconnect — verifies SEC-EDGE-7 rate limit
//   * selftest            unit tests for runner + scenario helpers (Sprint 55 / 58)
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
        "selftest" => RunSelfTest(),
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

    var snapshot = NickPerfRunner.RunAsync(
        scenario,
        GetReportFolder("health"),
        "health")
        .GetAwaiter().GetResult();

    return snapshot.fail == 0 ? 0 : 1;
}

static int RunCaseCreateScenario(IConfiguration config, LoadProfile profile)
{
    var scenario = CaseCreateScenario.Build(config, profile);
    if (scenario is null)
    {
        // Skip-on-misconfigured — already logged by Build.
        return 0;
    }

    var snapshot = NickPerfRunner.RunAsync(
        scenario,
        GetReportFolder("case-create"),
        "case-create")
        .GetAwaiter().GetResult();

    if (snapshot.ok == 0)
    {
        Console.WriteLine($"case-create: FAIL — 0 successful requests (all={snapshot.fail} failed). " +
                          "Target unreachable or returning errors. p99 acceptance gate cannot be evaluated.");
        return 1;
    }
    var gateExit = CaseCreateScenario.CheckAcceptanceGate(snapshot.p99, profile);
    return gateExit != 0 || snapshot.fail > snapshot.ok ? 1 : 0;
}

static int RunEdgeReplayScenario(IConfiguration config, LoadProfile profile)
{
    return EdgeReplayScenarioRunner.Run(config, profile, GetReportFolder);
}

static int RunEdgeReplayBacklogScenario(IConfiguration config)
{
    return EdgeReplayScenarioRunner.RunBacklog(config, GetReportFolder);
}

static int UnknownScenario(string name)
{
    Console.Error.WriteLine(
        $"Unknown scenario '{name}'. Available: health, case-create, edge-replay, edge-replay-backlog, selftest");
    return 1;
}

static int RunSelfTest()
{
    // Sprint 55 — light unit tests for the scenario helpers. Sprint 58 —
    // expanded to cover NickPerf runner internals (rate scheduling,
    // stats percentiles, report formatting). Doesn't need the runner to
    // run; just verifies the building blocks. Exits non-zero if any
    // assertion fails.
    Console.WriteLine("selftest: running scenario-helper + runner unit tests...");
    return NickERP.Perf.Tests.Scenarios.Helpers.HelperUnitTests.RunAll() == 0 ? 0 : 1;
}

static string GetReportFolder(string scenarioName)
{
    var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
    var path = Path.Combine(AppContext.BaseDirectory, "reports", date, scenarioName);
    Directory.CreateDirectory(path);
    return path;
}
