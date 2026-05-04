using Microsoft.Extensions.Configuration;
using NBomber.CSharp;
using NickERP.Perf.Tests;
using NickERP.Perf.Tests.Scenarios;

// Sprint 30 Phase V perf-test harness entry point.
//
// This is NOT a unit-test runner. It is a load-test runner that builds + reports
// against pilot-shaped traffic profiles defined in docs/perf/test-plan.md.
//
// Usage:
//   dotnet run --project tests/NickERP.Perf.Tests -- <scenario> [--profile 1x|5x|10x]
//
// Available scenarios:
//   * health       smoke against /healthz; verifies harness wiring + target reachability
//   * case-create  STUB; will exercise POST /api/inspection/cases. Requires Phase V fixtures
//   * edge-replay  STUB; will exercise POST /api/edge/replay. Requires Phase V fixtures
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
        "case-create" => RunCaseCreateScenarioStub(),
        "edge-replay" => RunEdgeReplayScenarioStub(),
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

    // Acceptance gate per test-plan §3.1: healthz p99 < 100ms (warn), no BLOCK.
    // Exit non-zero only if scenario itself failed.
    return stats.AllFailCount == 0 ? 0 : 1;
}

static int RunCaseCreateScenarioStub()
{
    Console.WriteLine("STUB: case-create scenario not yet executable.");
    Console.WriteLine("Phase V execution will:");
    Console.WriteLine("  1. Provision a test tenant + analyst user with CF Access JWT");
    Console.WriteLine("  2. Wire NBomber HTTP client with the JWT bearer");
    Console.WriteLine("  3. POST cases at the RPS profile (0.35 RPS pilot peak; 1.75 RPS at 5x)");
    Console.WriteLine("  4. Assert p99 < 1000ms acceptance gate (BLOCK at 2000ms per test-plan §3.1)");
    Console.WriteLine("See docs/perf/test-plan.md §11 'Open questions' for auth-mocking discussion.");
    return 0;
}

static int RunEdgeReplayScenarioStub()
{
    Console.WriteLine("STUB: edge-replay scenario not yet executable.");
    Console.WriteLine("Phase V execution will:");
    Console.WriteLine("  1. Provision a test edge node identity with per-edge HMAC key");
    Console.WriteLine("  2. Generate buffer fixtures (mixed event types: audit + scan-captured + scanner-status-changed)");
    Console.WriteLine("  3. Replay batches at the per-edge flush profile (every 30s, 5 events mean)");
    Console.WriteLine("  4. Stress test: 24h backlog reconnection (rate-limit verification per SEC-EDGE-7)");
    Console.WriteLine("  5. Assert p99 < 500ms (BLOCK at 1500ms)");
    return 0;
}

static int UnknownScenario(string name)
{
    Console.Error.WriteLine($"Unknown scenario '{name}'. Available: health, case-create, edge-replay");
    return 1;
}

static string GetReportFolder(string scenarioName)
{
    var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
    var path = Path.Combine(AppContext.BaseDirectory, "reports", date, scenarioName);
    Directory.CreateDirectory(path);
    return path;
}
