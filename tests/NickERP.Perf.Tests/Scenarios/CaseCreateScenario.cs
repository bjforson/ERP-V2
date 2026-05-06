using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Http.CSharp;
using NickERP.Perf.Tests.Auth;
using NickERP.Perf.Tests.Scenarios.Helpers;

namespace NickERP.Perf.Tests.Scenarios;

/// <summary>
/// Sprint 55 — case-create scenario for the inspection module's hot path
/// (<c>POST /api/inspection/cases</c> per test-plan §2.1 EP-001).
/// Replaces the Sprint 30 stub.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scenario shape.</b> Inject N requests/minute (per <see cref="LoadSimulationFactory.BuildCaseCreate"/>),
/// each carrying a freshly-generated ISO 6346 container number, scanner-
/// event reference, and AnalysisService claim. Auth: a CF-Access-shaped
/// JWT minted by <see cref="MockJwtBearerHandler"/> on a per-run basis.
/// The token is set as <c>Authorization: Bearer ...</c> on every request.
/// </para>
/// <para>
/// <b>Skip-on-misconfigured.</b> The scenario gracefully exits with code 0
/// when target / auth config is missing — see
/// <see cref="ShouldSkip"/>. This keeps CI green when the perf rig has
/// no target wired.
/// </para>
/// <para>
/// <b>Acceptance gate.</b> p99 &lt; <see cref="Pilot1xP99BlockMs"/> ms at 1x
/// profile (per test-plan §3.1 EP-001). The dispatcher checks the result
/// stats and exits non-zero if the gate is breached.
/// </para>
/// </remarks>
public static class CaseCreateScenario
{
    public const string ScenarioName = "case-create";

    /// <summary>Endpoint under test, per docs/perf/test-plan.md §2.1 EP-001.</summary>
    public const string EndpointPath = "/api/inspection/cases";

    /// <summary>Acceptance-gate latency in ms at 1x pilot peak. p99 must be ≤ this.</summary>
    public const int Pilot1xP99AcceptanceMs = 1000;

    /// <summary>BLOCK-pilot latency in ms at 1x pilot peak. p99 above this fails Phase V.</summary>
    public const int Pilot1xP99BlockMs = 2000;

    /// <summary>
    /// Sprint 52 / FU-perf-auth-mocking-decision — env var the operator
    /// sets to a real CF Access JWT for the spot-check scenario. When
    /// set, the scenario uses it verbatim; the API-side hits the real
    /// CF Access JWKS path. When unset, the mock signer at
    /// <see cref="MockJwtBearerHandler"/> produces a fresh signed token
    /// per run. Decision documented in <c>docs/perf/test-plan.md §11</c>.
    /// </summary>
    public const string RealBearerTokenEnvVar = "NICKERP_PERF_BEARER_TOKEN";

    /// <summary>
    /// Build the NBomber scenario. Returns null if the scenario should
    /// be skipped (missing target or no auth seam configured); the
    /// dispatcher logs + exits 0 in that case.
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
            log($"case-create: skipping — {reason}");
            return null;
        }

        var baseUrl = ResolveTargetBaseUrl(config);
        var url = baseUrl.TrimEnd('/') + EndpointPath;

        // Resolve auth — env var takes precedence (real-CF-Access spot
        // check); otherwise mint a fresh per-run mock JWT.
        var bearerToken = ResolveBearerToken(config);
        if (bearerToken is null)
        {
            // Should be unreachable because ShouldSkip catches this, but
            // belt + suspenders.
            log("case-create: skipping — no bearer token resolvable.");
            return null;
        }

        // Reuse one HttpClient + one RNG across the scenario. NBomber
        // dispatches the step concurrently per virtual user; both are
        // safe for Read (HttpClient is thread-safe; we lock around the
        // RNG below to keep payload generation deterministic).
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(config.GetSection("ScenarioDefaults:TimeoutMs").Get<int?>() ?? 5000)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var locationId = ResolveLocationId(config);
        var analysisServiceCode = config["CaseCreate:AnalysisServiceCode"] ?? "default";

        // RNG — seedable for deterministic test runs when needed.
        var seedConfig = config["CaseCreate:RandomSeed"];
        var rng = string.IsNullOrEmpty(seedConfig)
            ? new Random()
            : new Random(int.Parse(seedConfig));
        var rngLock = new object();

        var duration = TimeSpan.FromSeconds(
            config.GetSection("CaseCreate:DurationSeconds").Get<int?>() ?? 60);

        var scenario = Scenario.Create(ScenarioName, async _ =>
        {
            string body;
            lock (rngLock)
            {
                body = CaseCreatePayloadBuilder.Build(rng, locationId, analysisServiceCode);
            }

            var request = Http.CreateRequest("POST", url)
                .WithHeader("Accept", "application/json")
                .WithHeader("Content-Type", "application/json")
                .WithBody(new StringContent(body, Encoding.UTF8, "application/json"));

            var response = await Http.Send(http, request);
            return response;
        })
        .WithoutWarmUp()
        .WithLoadSimulations(LoadSimulationFactory.BuildCaseCreate(profile, duration));

        return scenario;
    }

    /// <summary>
    /// Check whether the scenario should skip — e.g. missing target,
    /// missing auth config. Public for unit-testability.
    /// </summary>
    /// <returns>
    /// (true, reason) if skipping; (false, null) otherwise.
    /// </returns>
    public static (bool Skip, string? Reason) ShouldSkip(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var target = ResolveTargetBaseUrl(config);
        if (string.IsNullOrWhiteSpace(target))
        {
            return (true, "no NICKERP_PERF_TargetBaseUrl set + appsettings has none");
        }

        var realToken = Environment.GetEnvironmentVariable(RealBearerTokenEnvVar);
        var mockEnabled = !string.IsNullOrWhiteSpace(config["Auth:MockJwt:Subject"]);
        if (string.IsNullOrWhiteSpace(realToken) && !mockEnabled)
        {
            return (true,
                $"no bearer token: {RealBearerTokenEnvVar} env var unset and Auth:MockJwt:Subject not configured.");
        }
        return (false, null);
    }

    /// <summary>
    /// Resolve the bearer token for this run. Returns null if no source
    /// is configured. Public for unit-testability.
    /// </summary>
    public static string? ResolveBearerToken(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var realToken = Environment.GetEnvironmentVariable(RealBearerTokenEnvVar);
        if (!string.IsNullOrWhiteSpace(realToken))
        {
            return realToken;
        }

        var subject = config["Auth:MockJwt:Subject"];
        var email = config["Auth:MockJwt:Email"] ?? "perf-analyst@nickerp-perf.example";
        var tenantIdRaw = config["Auth:MockJwt:TenantId"];
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var tenantId = long.TryParse(tenantIdRaw, out var t) ? t : 1L;

        // Per-run signing key. Disposed alongside the process (the
        // handler is held alive by the closure on the returned token,
        // but its RSA key gets garbage-collected when the process
        // exits). For a multi-scenario run we want one handler per
        // process so the per-API JWKS endpoint can validate every
        // scenario's tokens against the same kid.
        var handler = MockJwtBearerHandlerSingleton.Instance;
        return handler.ProduceBearerToken(subject, email, tenantId);
    }

    /// <summary>
    /// Resolve the target base URL: prefer scenario-specific override,
    /// then fall back to the harness default. Public for testing.
    /// </summary>
    public static string ResolveTargetBaseUrl(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config["CaseCreate:TargetBaseUrl"]
            ?? config["TargetBaseUrl"]
            ?? string.Empty;
    }

    /// <summary>
    /// Resolve the location guid for the test. Operators set
    /// <c>CaseCreate:LocationId</c> to the perf-seed-issued location id;
    /// we fall back to a stable test guid that won't collide with seeded
    /// data (so a 404 is unambiguous in baseline reports).
    /// </summary>
    public static Guid ResolveLocationId(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var raw = config["CaseCreate:LocationId"];
        if (!string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out var parsed))
            return parsed;
        return new Guid("00000000-0000-0000-0000-0000005ca5e0");
    }

    /// <summary>
    /// Acceptance-gate check against an NBomber stats result. Returns
    /// non-zero if the p99 exceeds <see cref="Pilot1xP99BlockMs"/> or any
    /// other BLOCK criterion fails. Public for testability.
    /// </summary>
    public static int CheckAcceptanceGate(double p99Ms, LoadProfile profile, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        // 1x is the gate; 5x relaxes 50%; 10x is informative.
        var blockMs = profile switch
        {
            LoadProfile.Pilot1x => Pilot1xP99BlockMs,
            LoadProfile.Tema5x => Pilot1xP99BlockMs * 1.5,
            LoadProfile.Stress10x => double.PositiveInfinity, // informative
            _ => Pilot1xP99BlockMs
        };
        if (p99Ms > blockMs)
        {
            log($"case-create: BLOCK — p99={p99Ms:F0}ms exceeds gate {blockMs:F0}ms at profile {profile}");
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
            log($"case-create: WARN — p99={p99Ms:F0}ms exceeds acceptance {acceptMs:F0}ms (BLOCK at {blockMs:F0}ms)");
        }
        else
        {
            log($"case-create: PASS — p99={p99Ms:F0}ms within acceptance {acceptMs:F0}ms");
        }
        return 0;
    }
}
