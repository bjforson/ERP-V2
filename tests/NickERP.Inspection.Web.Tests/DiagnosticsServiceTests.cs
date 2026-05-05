using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Telemetry;
using Xunit;
using FluentAssertions;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 33 / B7.2 — exercises <see cref="DiagnosticsService"/>.
///
/// <para>
/// Coverage:
/// <list type="bullet">
///   <item>Worker snapshots project every registered probe.</item>
///   <item>Worker snapshot survives a probe that throws — surfaces Unhealthy.</item>
///   <item>Overall worker health: any Unhealthy → Unhealthy; else any Degraded; else Healthy.</item>
///   <item>Empty probe set → Healthy.</item>
///   <item>Health snapshot returns the registered HealthCheckService report.</item>
///   <item>Health snapshot returns Unknown when no HealthCheckService registered.</item>
///   <item>LogViewerInfo: returns NotConfigured when neither key set.</item>
///   <item>LogViewerInfo: prefers SeqUiUrl over SeqUrl.</item>
///   <item>LogViewerInfo: falls back to SeqUrl when only that is set.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DiagnosticsServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Worker_snapshots_project_every_registered_probe()
    {
        var sp = BuildSp(probes: new IBackgroundServiceProbe[]
        {
            new FakeProbe("alpha", BackgroundServiceHealth.Healthy, ticks: 100, errors: 0),
            new FakeProbe("bravo", BackgroundServiceHealth.Degraded, ticks: 50, errors: 5, lastError: "boom"),
            new FakeProbe("charlie", BackgroundServiceHealth.Unhealthy, ticks: 0, errors: 0),
        });
        var svc = sp.GetRequiredService<DiagnosticsService>();

        var rows = svc.GetWorkerSnapshots();
        rows.Should().HaveCount(3);
        rows.Select(r => r.Name).Should().BeEquivalentTo(new[] { "alpha", "bravo", "charlie" });
        rows.Should().BeInAscendingOrder(r => r.Name, StringComparer.OrdinalIgnoreCase);
        rows.Single(r => r.Name == "bravo").LastError.Should().Be("boom");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Worker_snapshot_recovers_when_probe_throws()
    {
        var sp = BuildSp(probes: new IBackgroundServiceProbe[]
        {
            new FakeProbe("ok", BackgroundServiceHealth.Healthy),
            new ThrowingProbe("explody"),
        });
        var svc = sp.GetRequiredService<DiagnosticsService>();

        var rows = svc.GetWorkerSnapshots();
        rows.Should().HaveCount(2);
        var bad = rows.Single(r => r.Name == "explody");
        bad.Health.Should().Be("Unhealthy");
        bad.LastError.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Overall_worker_health_picks_worst_state()
    {
        var sp = BuildSp(probes: Array.Empty<IBackgroundServiceProbe>());
        var svc = sp.GetRequiredService<DiagnosticsService>();

        // Healthy on empty.
        svc.GetOverallWorkerHealth(Array.Empty<WorkerProbeSnapshot>()).Should().Be("Healthy");

        // All Healthy → Healthy.
        svc.GetOverallWorkerHealth(new[]
        {
            Snap("a", "Healthy"),
            Snap("b", "Healthy"),
        }).Should().Be("Healthy");

        // Degraded wins over Healthy.
        svc.GetOverallWorkerHealth(new[]
        {
            Snap("a", "Healthy"),
            Snap("b", "Degraded"),
        }).Should().Be("Degraded");

        // Unhealthy wins over Degraded.
        svc.GetOverallWorkerHealth(new[]
        {
            Snap("a", "Healthy"),
            Snap("b", "Degraded"),
            Snap("c", "Unhealthy"),
        }).Should().Be("Unhealthy");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Health_snapshot_runs_registered_health_checks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddHealthChecks()
            .AddCheck("alpha", () => HealthCheckResult.Healthy("alpha-ok"), tags: new[] { "ready" })
            .AddCheck("bravo", () => HealthCheckResult.Degraded("bravo-slow"))
            .AddCheck("charlie", () => HealthCheckResult.Unhealthy("charlie-bad"));
        services.AddSingleton<DiagnosticsService>();
        await using var sp = services.BuildServiceProvider();

        var svc = sp.GetRequiredService<DiagnosticsService>();
        var snapshot = await svc.GetHealthSnapshotAsync();

        snapshot.Entries.Should().HaveCount(3);
        snapshot.Entries.Should().BeInAscendingOrder(e => e.Name, StringComparer.OrdinalIgnoreCase);
        snapshot.Overall.Should().Be("Unhealthy", "any Unhealthy entry → overall Unhealthy");
        var alpha = snapshot.Entries.Single(e => e.Name == "alpha");
        alpha.Status.Should().Be("Healthy");
        alpha.Tags.Should().Contain("ready");
        snapshot.Entries.Single(e => e.Name == "charlie").Status.Should().Be("Unhealthy");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Health_snapshot_returns_Unknown_when_no_HealthCheckService_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddSingleton<DiagnosticsService>();
        await using var sp = services.BuildServiceProvider();

        var svc = sp.GetRequiredService<DiagnosticsService>();
        var snapshot = await svc.GetHealthSnapshotAsync();

        snapshot.Overall.Should().Be("Unknown");
        snapshot.Entries.Should().BeEmpty();
        snapshot.Note.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LogViewerInfo_returns_NotConfigured_when_neither_key_set()
    {
        var sp = BuildSp(probes: Array.Empty<IBackgroundServiceProbe>());
        var svc = sp.GetRequiredService<DiagnosticsService>();

        var info = svc.GetLogViewerInfo();
        info.IsConfigured.Should().BeFalse();
        info.SeqUrl.Should().BeNull();
        info.ConfiguredVia.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LogViewerInfo_prefers_SeqUiUrl_over_SeqUrl()
    {
        var sp = BuildSp(
            probes: Array.Empty<IBackgroundServiceProbe>(),
            config: new Dictionary<string, string?>
            {
                [DiagnosticsService.SeqUrlConfigKey] = "http://localhost:5341",
                [DiagnosticsService.SeqUiUrlConfigKey] = "https://logs.example.com/",
            });
        var svc = sp.GetRequiredService<DiagnosticsService>();

        var info = svc.GetLogViewerInfo();
        info.IsConfigured.Should().BeTrue();
        info.SeqUrl.Should().Be("https://logs.example.com/");
        info.ConfiguredVia.Should().Be(DiagnosticsService.SeqUiUrlConfigKey);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LogViewerInfo_falls_back_to_SeqUrl_when_UI_url_absent()
    {
        var sp = BuildSp(
            probes: Array.Empty<IBackgroundServiceProbe>(),
            config: new Dictionary<string, string?>
            {
                [DiagnosticsService.SeqUrlConfigKey] = "http://seq.local:5341",
            });
        var svc = sp.GetRequiredService<DiagnosticsService>();

        var info = svc.GetLogViewerInfo();
        info.IsConfigured.Should().BeTrue();
        info.SeqUrl.Should().Be("http://seq.local:5341");
        info.ConfiguredVia.Should().Be(DiagnosticsService.SeqUrlConfigKey);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static ServiceProvider BuildSp(
        IEnumerable<IBackgroundServiceProbe> probes,
        IDictionary<string, string?>? config = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
                .Build());
        foreach (var p in probes)
            services.AddSingleton(p);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddSingleton<DiagnosticsService>();
        return services.BuildServiceProvider();
    }

    private static WorkerProbeSnapshot Snap(string name, string health) =>
        new(name, health, null, null, 0, 0, null, null);

    // -----------------------------------------------------------------
    // Test probes
    // -----------------------------------------------------------------

    private sealed class FakeProbe : IBackgroundServiceProbe
    {
        private readonly BackgroundServiceState _state;
        public string WorkerName { get; }

        public FakeProbe(string name, BackgroundServiceHealth health, long ticks = 0, long errors = 0, string? lastError = null)
        {
            WorkerName = name;
            _state = new BackgroundServiceState(
                LastTickAt: ticks > 0 ? DateTimeOffset.UtcNow : null,
                LastSuccessAt: ticks > 0 ? DateTimeOffset.UtcNow : null,
                TickCount: ticks,
                ErrorCount: errors,
                LastError: lastError,
                LastErrorAt: lastError is null ? null : DateTimeOffset.UtcNow,
                Health: health);
        }

        public BackgroundServiceState GetState() => _state;
    }

    private sealed class ThrowingProbe : IBackgroundServiceProbe
    {
        public string WorkerName { get; }
        public ThrowingProbe(string name) { WorkerName = name; }
        public BackgroundServiceState GetState() => throw new InvalidOperationException("boom");
    }
}
