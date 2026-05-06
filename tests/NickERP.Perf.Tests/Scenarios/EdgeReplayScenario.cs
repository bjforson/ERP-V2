using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;
using NickERP.Perf.Tests.Scenarios.Helpers;

namespace NickERP.Perf.Tests.Scenarios;

/// <summary>
/// Sprint 55 — edge-replay scenario for the central-write hot path
/// (<c>POST /api/edge/replay</c> per test-plan §2.1 EP-005). Replaces
/// the Sprint 30 stub.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two NBomber scenarios.</b>
/// <list type="bullet">
///   <item><see cref="Build"/> — steady-state replay at the per-edge
///         flush cadence. Profiles 1x/5x/10x per
///         <see cref="LoadSimulationFactory.BuildEdgeReplay"/>.</item>
///   <item><see cref="BuildBacklog"/> — 24h-backlog reconnection.
///         Submits a high burst of batches to verify the central rate-
///         limit holds (per Sprint 30 SEC-EDGE-7). Some failures are
///         expected; the run report quantifies them.</item>
/// </list>
/// </para>
/// <para>
/// <b>Auth.</b> The endpoint requires the per-edge HMAC API key in the
/// <c>X-Edge-Api-Key</c> header. Operators set
/// <see cref="EdgeHmacKeyEnvVar"/>; missing-key skips the scenario
/// gracefully (no broken CI).
/// </para>
/// <para>
/// <b>Acceptance gate.</b> p99 &lt; <see cref="Pilot1xP99BlockMs"/> ms at 1x
/// per test-plan §3.1 EP-005. The dispatcher checks the result and
/// exits non-zero if the gate is breached.
/// </para>
/// <para>
/// <b>Mixed event types.</b> Each batch carries 1-N events with a
/// distribution across the three Sprint 17 hints (audit-replay /
/// scan-captured / scanner-status-changed). Per test-plan §5.
/// </para>
/// </remarks>
public static class EdgeReplayScenario
{
    public const string ScenarioName = "edge-replay";
    public const string BacklogScenarioName = "edge-replay-backlog";

    /// <summary>Endpoint under test, per docs/perf/test-plan.md §2.1 EP-005.</summary>
    public const string EndpointPath = "/api/edge/replay";

    /// <summary>Acceptance-gate latency in ms at 1x pilot peak.</summary>
    public const int Pilot1xP99AcceptanceMs = 500;

    /// <summary>BLOCK-pilot latency in ms at 1x pilot peak.</summary>
    public const int Pilot1xP99BlockMs = 1500;

    /// <summary>HTTP header carrying the per-edge HMAC API key (Sprint 13).</summary>
    public const string ApiKeyHeader = "X-Edge-Api-Key";

    /// <summary>
    /// Sprint 30 SEC-EDGE-7 — env var carrying the per-edge HMAC API
    /// key. When unset the scenario skips with a logged note.
    /// </summary>
    public const string EdgeHmacKeyEnvVar = "NICKERP_PERF_EDGE_HMAC_KEY";

    /// <summary>
    /// Build the steady-state edge-replay NBomber scenario. Returns
    /// null when scenario should skip (missing target / missing HMAC
    /// key).
    /// </summary>
    public static ScenarioProps? Build(
        IConfiguration config,
        LoadProfile profile,
        Action<string>? log = null)
    {
        log ??= Console.WriteLine;

        var (skip, reason) = ShouldSkip(config);
        if (skip)
        {
            log($"edge-replay: skipping — {reason}");
            return null;
        }

        var url = ResolveEndpointUrl(config);
        var apiKey = ResolveEdgeHmacKey();
        var (edgeNodeId, tenantId, meanEvents, maxEvents) = ResolveBatchConfig(config);
        var rng = ResolveRng(config);
        var rngLock = new object();

        var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(
                config.GetSection("ScenarioDefaults:TimeoutMs").Get<int?>() ?? 5000)
        };
        http.DefaultRequestHeaders.Add(ApiKeyHeader, apiKey);

        var duration = TimeSpan.FromSeconds(
            config.GetSection("EdgeReplay:DurationSeconds").Get<int?>() ?? 60);

        var scenario = Scenario.Create(ScenarioName, async _ =>
        {
            string body;
            lock (rngLock)
            {
                body = EdgeReplayPayloadBuilder.BuildBatch(
                    rng, edgeNodeId, tenantId, meanEvents, maxEvents);
            }

            var request = Http.CreateRequest("POST", url)
                .WithHeader("Accept", "application/json")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(body, Encoding.UTF8, "application/json"));

            var response = await Http.Send(http, request);
            return response;
        })
        .WithoutWarmUp()
        .WithLoadSimulations(LoadSimulationFactory.BuildEdgeReplay(profile, duration));

        return scenario;
    }

    /// <summary>
    /// Build the 24h-backlog reconnection scenario. Submits a high
    /// burst over a short duration to verify the central rate-limit
    /// holds (per Sprint 30 SEC-EDGE-7). Returns null on misconfigured.
    /// </summary>
    public static ScenarioProps? BuildBacklog(
        IConfiguration config,
        Action<string>? log = null)
    {
        log ??= Console.WriteLine;

        var (skip, reason) = ShouldSkip(config);
        if (skip)
        {
            log($"edge-replay-backlog: skipping — {reason}");
            return null;
        }

        var url = ResolveEndpointUrl(config);
        var apiKey = ResolveEdgeHmacKey();
        var (edgeNodeId, tenantId, meanEvents, maxEvents) = ResolveBatchConfig(config);
        var rng = ResolveRng(config);
        var rngLock = new object();

        var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(
                config.GetSection("ScenarioDefaults:TimeoutMs").Get<int?>() ?? 5000)
        };
        http.DefaultRequestHeaders.Add(ApiKeyHeader, apiKey);

        // Simulate a 24h backlog: 24h × 60min × 2 flushes/min = 2880
        // batches typically queued, but cap at the configured size to
        // keep the test bounded. Larger meanEvents → fewer batches
        // needed to drain the same event count.
        var configured = config.GetSection("EdgeReplay:BacklogEventCount").Get<int?>() ?? 8640;
        var batches = Math.Max(60, configured / Math.Max(1, meanEvents));
        var duration = TimeSpan.FromSeconds(
            config.GetSection("EdgeReplay:BacklogDurationSeconds").Get<int?>() ?? 60);

        var scenario = Scenario.Create(BacklogScenarioName, async _ =>
        {
            string body;
            lock (rngLock)
            {
                // Backlog flushes typically max out the per-batch size
                // (an offline edge has accumulated lots of events). Use
                // maxEvents for the count to reflect that.
                body = EdgeReplayPayloadBuilder.BuildBatch(
                    rng, edgeNodeId, tenantId,
                    meanEvents: maxEvents, maxEvents: maxEvents);
            }

            var request = Http.CreateRequest("POST", url)
                .WithHeader("Accept", "application/json")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(body, Encoding.UTF8, "application/json"));

            var response = await Http.Send(http, request);
            return response;
        })
        .WithoutWarmUp()
        .WithLoadSimulations(LoadSimulationFactory.BuildEdgeBacklog(duration, batches));

        return scenario;
    }

    /// <summary>
    /// Skip-on-misconfigured check. Public for testability.
    /// </summary>
    public static (bool Skip, string? Reason) ShouldSkip(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var target = ResolveTargetBaseUrl(config);
        if (string.IsNullOrWhiteSpace(target))
        {
            return (true, "no NICKERP_PERF_TargetBaseUrl set + appsettings has none");
        }

        var apiKey = ResolveEdgeHmacKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (true,
                $"no edge HMAC key: env var {EdgeHmacKeyEnvVar} unset. " +
                "Set it to the per-edge API key issued by the central admin flow.");
        }

        return (false, null);
    }

    /// <summary>Resolve the edge HMAC API key. Public for testing.</summary>
    public static string? ResolveEdgeHmacKey()
        => Environment.GetEnvironmentVariable(EdgeHmacKeyEnvVar);

    /// <summary>Resolve the target base URL — scenario-specific override wins. Public for testing.</summary>
    public static string ResolveTargetBaseUrl(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config["EdgeReplay:TargetBaseUrl"]
            ?? config["Endpoints:InspectionWebBaseUrl"]
            ?? config["TargetBaseUrl"]
            ?? string.Empty;
    }

    /// <summary>Resolve the full endpoint URL. Public for testing.</summary>
    public static string ResolveEndpointUrl(IConfiguration config)
    {
        var baseUrl = ResolveTargetBaseUrl(config);
        var path = config["Endpoints:EdgeReplayPath"] ?? EndpointPath;
        return baseUrl.TrimEnd('/') + path;
    }

    /// <summary>Resolve batch-shape config: edge id, tenant, mean / max events. Public for testing.</summary>
    public static (string EdgeNodeId, long TenantId, int MeanEvents, int MaxEvents) ResolveBatchConfig(
        IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var edgeNodeId = config["EdgeReplay:EdgeNodeId"] ?? "perf-edge-001";
        var tenantId = config.GetSection("EdgeReplay:TenantId").Get<long?>() ?? 1L;
        var meanEvents = config.GetSection("EdgeReplay:MeanEventsPerBatch").Get<int?>() ?? 5;
        var maxEvents = config.GetSection("EdgeReplay:MaxEventsPerBatch").Get<int?>() ?? 20;
        return (edgeNodeId, tenantId, meanEvents, maxEvents);
    }

    /// <summary>Resolve a deterministic RNG when seeded; otherwise fresh.</summary>
    private static Random ResolveRng(IConfiguration config)
    {
        var seed = config["EdgeReplay:RandomSeed"];
        return string.IsNullOrEmpty(seed) ? new Random() : new Random(int.Parse(seed));
    }

    /// <summary>
    /// Acceptance-gate check against an NBomber stats result. 1x is the
    /// gate; 5x relaxes 50%; 10x is informative. Public for testability.
    /// </summary>
    public static int CheckAcceptanceGate(double p99Ms, LoadProfile profile, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var blockMs = profile switch
        {
            LoadProfile.Pilot1x => Pilot1xP99BlockMs,
            LoadProfile.Tema5x => Pilot1xP99BlockMs * 1.5,
            LoadProfile.Stress10x => double.PositiveInfinity,
            _ => Pilot1xP99BlockMs
        };
        if (p99Ms > blockMs)
        {
            log($"edge-replay: BLOCK — p99={p99Ms:F0}ms exceeds gate {blockMs:F0}ms at profile {profile}");
            return 1;
        }
        var acceptMs = profile switch
        {
            LoadProfile.Pilot1x => Pilot1xP99AcceptanceMs,
            LoadProfile.Tema5x => Pilot1xP99AcceptanceMs * 1.5,
            _ => Pilot1xP99AcceptanceMs
        };
        if (p99Ms > acceptMs)
        {
            log($"edge-replay: WARN — p99={p99Ms:F0}ms exceeds acceptance {acceptMs:F0}ms (BLOCK at {blockMs:F0}ms)");
        }
        else
        {
            log($"edge-replay: PASS — p99={p99Ms:F0}ms within acceptance {acceptMs:F0}ms");
        }
        return 0;
    }
}
