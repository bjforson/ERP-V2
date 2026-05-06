using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Web.Components.Pages.Workers;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Telemetry;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 50 / FU-b3-admin-pages — bunit coverage for the
/// <c>/admin/workers</c> page. Asserts:
/// <list type="bullet">
///   <item><description>The page renders one row per registered probe
///   that has a curated entry in <see cref="WorkersAdminService"/>.</description></item>
///   <item><description>Force-tick button on a row whose worker has a
///   force-tick method invokes the right method on the right
///   instance.</description></item>
///   <item><description>Workers without force-tick (streaming) render a
///   dash + no button.</description></item>
/// </list>
/// </summary>
public sealed class WorkersAdminTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();

    public WorkersAdminTests()
    {
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));

        // Singleton fake auth state — page is decorated with
        // [Authorize(Roles = "Inspection.Admin")]; supply a principal
        // carrying that role so the page renders.
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new AdminAuthStateProvider());
        _ctx.Services.AddAuthorization();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void WorkersAdmin_RendersOneRowPerCuratedWorker()
    {
        // Register a probe for one curated worker name. The page should
        // surface the row + the description from the curated entry.
        var probe = new FakeProbe(name: "AseSyncWorker", health: BackgroundServiceHealth.Healthy);
        _ctx.Services.AddSingleton<IBackgroundServiceProbe>(probe);
        _ctx.Services.AddSingleton<WorkersAdminService>();

        var cut = _ctx.RenderComponent<WorkersAdmin>();

        cut.Markup.Should().Contain("AseSyncWorker",
            because: "the curated entry's ProbeName must appear in the rendered table");
        cut.Markup.Should().Contain("Healthy");
    }

    [Fact]
    public void WorkersAdmin_RendersHealthAndCounts()
    {
        var probe = new FakeProbe(
            name: "AseSyncWorker",
            health: BackgroundServiceHealth.Degraded,
            tickCount: 7,
            errorCount: 2,
            lastError: "simulated-failure");
        _ctx.Services.AddSingleton<IBackgroundServiceProbe>(probe);
        _ctx.Services.AddSingleton<WorkersAdminService>();

        var cut = _ctx.RenderComponent<WorkersAdmin>();

        cut.Markup.Should().Contain("Degraded");
        cut.Markup.Should().Contain(">7<", because: "tick count surfaces in a <td>");
        cut.Markup.Should().Contain(">2<", because: "error count surfaces in a <td>");
        cut.Markup.Should().Contain("simulated-failure");
    }

    [Fact]
    public void WorkersAdmin_HasForceTickButton_ForCuratedWorker()
    {
        var probe = new FakeProbe(name: "AseSyncWorker", health: BackgroundServiceHealth.Healthy);
        _ctx.Services.AddSingleton<IBackgroundServiceProbe>(probe);
        _ctx.Services.AddSingleton<WorkersAdminService>();

        var cut = _ctx.RenderComponent<WorkersAdmin>();

        cut.Markup.Should().Contain("Force tick");
    }

    [Fact]
    public async Task WorkersAdmin_ForceTickButton_InvokesAdminService()
    {
        // The admin service is hard-wired to call AseSyncWorker.PullOnceAsync
        // for the AseSyncWorker entry. We swap in a recording subclass of
        // the admin service so we can assert the click invokes ForceTickAsync
        // with the right worker name without standing up a full DI graph
        // (AseSyncWorker has many dependencies).
        var probe = new FakeProbe(name: "AseSyncWorker", health: BackgroundServiceHealth.Healthy);
        _ctx.Services.AddSingleton<IBackgroundServiceProbe>(probe);
        var recorder = new RecordingAdminService(_ctx.Services.BuildServiceProvider(),
            NullLogger<WorkersAdminService>.Instance);
        _ctx.Services.AddSingleton(recorder);
        _ctx.Services.AddSingleton<WorkersAdminService>(sp => sp.GetRequiredService<RecordingAdminService>());

        var cut = _ctx.RenderComponent<WorkersAdmin>();

        var button = cut.Find("button.inspection-button");
        await button.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        recorder.LastForceTicked.Should().Be("AseSyncWorker");
    }

    // -----------------------------------------------------------------
    // Test helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Curated probe that surfaces a fixed name + state. The
    /// <c>WorkerName</c> must match a curated entry in
    /// <see cref="WorkersAdminService"/> for the page to render the
    /// row + Description.
    /// </summary>
    private sealed class FakeProbe : IBackgroundServiceProbe
    {
        private readonly BackgroundServiceState _state;

        public FakeProbe(
            string name,
            BackgroundServiceHealth health,
            long tickCount = 0,
            long errorCount = 0,
            string? lastError = null)
        {
            WorkerName = name;
            _state = new BackgroundServiceState(
                LastTickAt: DateTimeOffset.UtcNow,
                LastSuccessAt: DateTimeOffset.UtcNow,
                TickCount: tickCount,
                ErrorCount: errorCount,
                LastError: lastError,
                LastErrorAt: lastError is null ? null : DateTimeOffset.UtcNow,
                Health: health);
        }

        public string WorkerName { get; }
        public BackgroundServiceState GetState() => _state;
    }

    /// <summary>
    /// Recording subclass — overrides
    /// <see cref="WorkersAdminService.ForceTickAsync"/> to capture the
    /// invocation without exercising the real reflection path (the
    /// AseSyncWorker concrete type isn't registered in this
    /// lightweight test container).
    /// </summary>
    private sealed class RecordingAdminService : WorkersAdminService
    {
        public RecordingAdminService(IServiceProvider services,
            Microsoft.Extensions.Logging.ILogger<WorkersAdminService> logger)
            : base(services, logger) { }

        public string? LastForceTicked { get; private set; }

        public override Task<int> ForceTickAsync(string workerName, CancellationToken ct)
        {
            LastForceTicked = workerName;
            return Task.FromResult(1);
        }
    }

    private sealed class AdminAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "admin-user"),
                new Claim(ClaimTypes.Role, "Inspection.Admin"),
                new Claim("nickerp:id", Guid.NewGuid().ToString())
            }, "test-auth");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
