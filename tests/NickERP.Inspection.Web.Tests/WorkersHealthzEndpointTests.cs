using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Inspection.Web.Endpoints;
using NickERP.Platform.Telemetry;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 9 / FU-host-status — exercises <see cref="WorkersHealthzEndpoint"/>.
///
/// <para>
/// Uses a hand-built <see cref="DefaultHttpContext"/> with an
/// in-memory <see cref="IServiceProvider"/> wiring fake
/// <see cref="IBackgroundServiceProbe"/> instances. Asserts:
/// </para>
/// <list type="bullet">
///   <item>Endpoint returns 200 with the expected JSON shape when every probe is Healthy.</item>
///   <item>Aggregation rules: Unhealthy beats Degraded beats Healthy.</item>
///   <item>The mapped route carries an authorization requirement
///         (<c>RequireAuthorization()</c>) — anonymous requests would
///         be blocked by the routing layer in production, even though
///         the handler itself returns 200.</item>
/// </list>
///
/// <para>
/// Drives fake probes rather than spinning up real workers — the
/// workers' internal state machinery (DbContext, tenancy interceptor,
/// disk image store) is irrelevant to the endpoint's contract. The
/// real workers' probe wiring is exercised by their own existing test
/// suites + the integration smoke test.
/// </para>
/// </summary>
public sealed class WorkersHealthzEndpointTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Endpoint_returns_200_and_expected_shape_when_all_probes_healthy()
    {
        var now = DateTimeOffset.Parse("2026-04-29T12:00:00Z");

        var probes = new IBackgroundServiceProbe[]
        {
            new FakeProbe("PreRenderWorker", new BackgroundServiceState(
                LastTickAt: now.AddSeconds(-1),
                LastSuccessAt: now.AddSeconds(-1),
                TickCount: 1234,
                ErrorCount: 0,
                LastError: null,
                LastErrorAt: null,
                Health: BackgroundServiceHealth.Healthy)),
            new FakeProbe("AuditNotificationProjector", new BackgroundServiceState(
                LastTickAt: now.AddSeconds(-2),
                LastSuccessAt: now.AddSeconds(-2),
                TickCount: 50,
                ErrorCount: 0,
                LastError: null,
                LastErrorAt: null,
                Health: BackgroundServiceHealth.Healthy)),
        };

        var http = BuildHttpContextWith(probes);

        var result = WorkersHealthzEndpoint.GetAsync(http);

        result.GetType().Name.Should().Contain("Ok");
        var body = ExtractValue<WorkersHealthDto>(result);
        body.Should().NotBeNull();
        body!.Overall.Should().Be("Healthy");
        body.Workers.Should().HaveCount(2);

        // Sorted alphabetically by name for stable ops dashboard output.
        body.Workers[0].Name.Should().Be("AuditNotificationProjector");
        body.Workers[0].Health.Should().Be("Healthy");
        body.Workers[0].TickCount.Should().Be(50);
        body.Workers[0].ErrorCount.Should().Be(0);
        body.Workers[0].LastError.Should().BeNull();

        body.Workers[1].Name.Should().Be("PreRenderWorker");
        body.Workers[1].Health.Should().Be("Healthy");
        body.Workers[1].TickCount.Should().Be(1234);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Aggregate_overall_is_Unhealthy_when_any_probe_Unhealthy()
    {
        // Mix: one Healthy, one Degraded, one Unhealthy → Unhealthy wins.
        var probes = new IBackgroundServiceProbe[]
        {
            FakeWith("A", BackgroundServiceHealth.Healthy),
            FakeWith("B", BackgroundServiceHealth.Degraded),
            FakeWith("C", BackgroundServiceHealth.Unhealthy),
        };

        var result = WorkersHealthzEndpoint.GetAsync(BuildHttpContextWith(probes));
        var body = ExtractValue<WorkersHealthDto>(result)!;
        body.Overall.Should().Be("Unhealthy");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Aggregate_overall_is_Degraded_when_any_Degraded_but_none_Unhealthy()
    {
        var probes = new IBackgroundServiceProbe[]
        {
            FakeWith("A", BackgroundServiceHealth.Healthy),
            FakeWith("B", BackgroundServiceHealth.Degraded),
            FakeWith("C", BackgroundServiceHealth.Healthy),
        };

        var result = WorkersHealthzEndpoint.GetAsync(BuildHttpContextWith(probes));
        var body = ExtractValue<WorkersHealthDto>(result)!;
        body.Overall.Should().Be("Degraded");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Aggregate_overall_is_Healthy_when_no_probes_registered()
    {
        // Defensive — host with zero workers should not return Unhealthy
        // just because no probe has ticked. Mirrors the empty-set rule on
        // any "any of these is bad" predicate.
        var result = WorkersHealthzEndpoint.GetAsync(BuildHttpContextWith(Array.Empty<IBackgroundServiceProbe>()));
        var body = ExtractValue<WorkersHealthDto>(result)!;
        body.Overall.Should().Be("Healthy");
        body.Workers.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Mapped_route_carries_authorization_requirement()
    {
        // Build a minimal WebApplication that wires only what the
        // endpoint needs (routing + auth services + the
        // MapWorkersHealthzEndpoint() registration), then walk the
        // WebApplication's data-source aggregation to confirm the route
        // carries an [Authorize] metadata. Anonymous callers would be
        // rejected by the routing layer in production even though the
        // handler itself returns 200 — this guards against a future
        // regression where someone drops the `.RequireAuthorization()`
        // call.
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.MapWorkersHealthzEndpoint();

        // WebApplication itself implements IEndpointRouteBuilder; its
        // DataSources collection holds the endpoints we just mapped.
        // Resolving EndpointDataSource off app.Services returns a
        // composite-but-empty source until the host starts, so walk
        // the builder's own data sources instead.
        IEndpointRouteBuilder routeBuilder = app;
        var endpoints = routeBuilder.DataSources.SelectMany(ds => ds.Endpoints).ToList();

        var endpoint = endpoints
            .OfType<RouteEndpoint>()
            .FirstOrDefault(e =>
                (e.RoutePattern.RawText ?? string.Empty)
                    .TrimStart('/')
                    .Equals("healthz/workers", StringComparison.Ordinal));

        endpoint.Should().NotBeNull(
            "the /healthz/workers route should be registered; enumerated routes: "
            + string.Join(", ", endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText)));
        endpoint!.Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull(
            "the /healthz/workers endpoint must require authorization (RequireAuthorization() applied)");
    }

    // -- helpers --------------------------------------------------------

    private static HttpContext BuildHttpContextWith(IEnumerable<IBackgroundServiceProbe> probes)
    {
        var services = new ServiceCollection();
        foreach (var probe in probes)
            services.AddSingleton(probe);
        var sp = services.BuildServiceProvider();

        return new DefaultHttpContext { RequestServices = sp };
    }

    private static FakeProbe FakeWith(string name, BackgroundServiceHealth health)
    {
        var now = DateTimeOffset.UtcNow;
        return new FakeProbe(name, new BackgroundServiceState(
            LastTickAt: now,
            LastSuccessAt: health == BackgroundServiceHealth.Healthy ? now : null,
            TickCount: 1,
            ErrorCount: health == BackgroundServiceHealth.Healthy ? 0 : 1,
            LastError: health == BackgroundServiceHealth.Healthy ? null : "boom",
            LastErrorAt: health == BackgroundServiceHealth.Healthy ? null : now,
            Health: health));
    }

    private static T? ExtractValue<T>(IResult result) where T : class
    {
        var prop = result.GetType().GetProperty("Value");
        return prop?.GetValue(result) as T;
    }

    private sealed record FakeProbe(string WorkerName, BackgroundServiceState State) : IBackgroundServiceProbe
    {
        public BackgroundServiceState GetState() => State;
    }
}
