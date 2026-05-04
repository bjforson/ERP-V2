using Microsoft.Extensions.Configuration;
using NBomber.CSharp;
using NBomber.Contracts;
using NBomber.Http.CSharp;

namespace NickERP.Perf.Tests.Scenarios;

/// <summary>
/// Smoke scenario — hits the portal's <c>/healthz</c> endpoint at a low rate.
/// Verifies the harness wiring + target reachability + reporting path. Acts as
/// the dev-time sanity check that NBomber + the report folder layout work.
/// </summary>
/// <remarks>
/// <para>
/// This is the only scenario that runs without Phase V test-fixture
/// preparation. It does NOT exercise any real load profile.
/// </para>
/// </remarks>
public static class HealthEndpointScenario
{
    public static ScenarioProps Build(IConfiguration config, LoadProfile profile)
    {
        var baseUrl = config["TargetBaseUrl"] ?? "http://localhost:5400";
        var path = config["HealthzPath"] ?? "/healthz";
        var url = baseUrl.TrimEnd('/') + path;

        using var http = new HttpClient();

        var scenario = Scenario.Create("health", async ctx =>
        {
            var request = Http.CreateRequest("GET", url)
                .WithHeader("Accept", "application/json");

            var response = await Http.Send(http, request);
            return response;
        })
        .WithoutWarmUp()
        .WithLoadSimulations(BuildLoadSimulation(profile));

        return scenario;
    }

    private static LoadSimulation BuildLoadSimulation(LoadProfile profile)
    {
        // Healthz is a probe; rate scales modestly with profile. Per test-plan §2.1 EP-008.
        return profile switch
        {
            LoadProfile.Pilot1x => Simulation.Inject(rate: 1, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30)),
            LoadProfile.Tema5x => Simulation.Inject(rate: 5, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30)),
            LoadProfile.Stress10x => Simulation.Inject(rate: 10, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(60)),
            _ => Simulation.Inject(rate: 1, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30)),
        };
    }
}
