using Microsoft.Extensions.Configuration;
using NickERP.Perf.Tests.Runner;
using NickERP.Perf.Tests.Runner.Http;

namespace NickERP.Perf.Tests.Scenarios;

/// <summary>
/// Smoke scenario — hits the portal's <c>/healthz</c> endpoint at a low
/// rate. Verifies the harness wiring + target reachability + reporting
/// path. Acts as the dev-time sanity check that the homegrown NickPerf
/// runner + the report folder layout work.
/// </summary>
/// <remarks>
/// <para>
/// This is the only scenario that runs without Phase V test-fixture
/// preparation. It does NOT exercise any real load profile.
/// </para>
/// <para>
/// Sprint 58 — ported from NBomber's <c>Scenario.Create</c> /
/// <c>Http.CreateRequest</c> shape to <see cref="NickPerfScenario"/>;
/// the HTTP call now goes through <see cref="NickPerfHttp.GetAsync"/>.
/// Behaviour is unchanged (same URL, same rate per profile).
/// </para>
/// </remarks>
public static class HealthEndpointScenario
{
    public const string ScenarioName = "health";

    public static NickPerfScenario Build(IConfiguration config, LoadProfile profile)
    {
        var baseUrl = config["TargetBaseUrl"] ?? "http://localhost:5400";
        var path = config["HealthzPath"] ?? "/healthz/live";
        var url = baseUrl.TrimEnd('/') + path;

        // Long-lived per-scenario HttpClient; the runner owns the
        // scenario lifetime so we reuse this client across every step.
        var http = new HttpClient();

        return new NickPerfScenario
        {
            Name = ScenarioName,
            LoadProfile = BuildLoadProfile(profile),
            RunStep = ct => NickPerfHttp.GetAsync(http, url, ct: ct)
        };
    }

    private static NickPerfLoadProfile BuildLoadProfile(LoadProfile profile)
    {
        // Healthz is a probe; rate scales modestly with profile. Per test-plan §2.1 EP-008.
        return profile switch
        {
            LoadProfile.Pilot1x => new NickPerfLoadProfile { Rate = 1, Interval = TimeSpan.FromSeconds(1), Duration = TimeSpan.FromSeconds(30) },
            LoadProfile.Tema5x => new NickPerfLoadProfile { Rate = 5, Interval = TimeSpan.FromSeconds(1), Duration = TimeSpan.FromSeconds(30) },
            LoadProfile.Stress10x => new NickPerfLoadProfile { Rate = 10, Interval = TimeSpan.FromSeconds(1), Duration = TimeSpan.FromSeconds(60) },
            _ => new NickPerfLoadProfile { Rate = 1, Interval = TimeSpan.FromSeconds(1), Duration = TimeSpan.FromSeconds(30) }
        };
    }
}
