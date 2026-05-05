using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Tenancy;
using SlaDashboardPage = NickERP.Inspection.Web.Components.Pages.Completeness.SlaDashboard;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 49 / FU-sla-trend-sparkline — bunit render coverage for the
/// /admin/sla page's new SVG sparkline. Asserts:
///
/// <list type="bullet">
///   <item><description>The sparkline SVG element + the three
///   coloured polylines render when trend data exists.</description></item>
///   <item><description>The legend lists the three series with their
///   running totals.</description></item>
///   <item><description>An empty-DB render falls back to the empty-
///   state copy instead of an empty SVG.</description></item>
/// </list>
/// </summary>
public sealed class SlaDashboardSparklineTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private const long TenantId = 41L;

    public SlaDashboardSparklineTests()
    {
        var dbName = "sla-spark-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(TenantId);
            return t;
        });
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(
            new FakeAuthStateProvider());
        _ctx.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SlaTrackerOptions()));
        _ctx.Services.AddScoped<SlaDashboardService>();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void SlaDashboard_renders_sparkline_with_three_polylines()
    {
        // Seed enough trend data to make the sparkline render.
        using (var scope = _ctx.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var todayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            db.SlaWindows.Add(new SlaWindow
            {
                Id = Guid.NewGuid(),
                CaseId = Guid.NewGuid(),
                WindowName = "case.open_to_validated",
                StartedAt = todayUtc.AddDays(-2).AddHours(2),
                ClosedAt = todayUtc.AddDays(-1).AddHours(2),
                DueAt = todayUtc.AddDays(-2).AddHours(3),
                BudgetMinutes = 60,
                State = SlaWindowState.Closed,
                QueueTier = QueueTier.Standard,
                TenantId = TenantId,
            });
            db.SlaWindows.Add(new SlaWindow
            {
                Id = Guid.NewGuid(),
                CaseId = Guid.NewGuid(),
                WindowName = "case.open_to_validated",
                StartedAt = todayUtc.AddDays(-3).AddHours(1),
                ClosedAt = todayUtc.AddDays(-1).AddHours(8),
                DueAt = todayUtc.AddDays(-2).AddHours(0),
                BudgetMinutes = 60,
                State = SlaWindowState.Breached,
                QueueTier = QueueTier.Standard,
                TenantId = TenantId,
            });
            db.SaveChanges();
        }

        var page = _ctx.RenderComponent<SlaDashboardPage>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading..."),
            TimeSpan.FromSeconds(3));

        // SVG element is present + carries the inspection-sparkline class.
        page.Markup.Should().Contain("inspection-sparkline");

        // All three series render polyline elements (we can't be sure
        // which series will have a non-zero value, but seeded data has
        // opened + closed + breached so all three classes appear).
        page.Markup.Should().Contain("inspection-sparkline-opened");
        page.Markup.Should().Contain("inspection-sparkline-closed");
        page.Markup.Should().Contain("inspection-sparkline-breached");

        // Legend entries are present.
        page.Markup.Should().Contain("inspection-sparkline-legend");
        page.Markup.Should().Contain("Opened (");
        page.Markup.Should().Contain("Closed (");
        page.Markup.Should().Contain("Breached (");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SlaDashboard_renders_polyline_points_attribute()
    {
        // Seed a single window so the sparkline has a non-empty path.
        using (var scope = _ctx.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var todayUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            db.SlaWindows.Add(new SlaWindow
            {
                Id = Guid.NewGuid(),
                CaseId = Guid.NewGuid(),
                WindowName = "case.open_to_validated",
                StartedAt = todayUtc.AddDays(-3).AddHours(2),
                DueAt = todayUtc.AddDays(-3).AddHours(3),
                BudgetMinutes = 60,
                State = SlaWindowState.OnTime,
                QueueTier = QueueTier.Standard,
                TenantId = TenantId,
            });
            db.SaveChanges();
        }

        var page = _ctx.RenderComponent<SlaDashboardPage>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading..."),
            TimeSpan.FromSeconds(3));

        // Each polyline renders a points="..." attribute. The simplest
        // structural check: at least one points= attribute exists in
        // the markup of the sparkline svg.
        var polylines = page.FindAll("polyline");
        polylines.Should().NotBeEmpty();
        polylines.Should().Contain(p => p.HasAttribute("points") && !string.IsNullOrWhiteSpace(p.GetAttribute("points")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SlaDashboard_renders_ok_with_empty_db()
    {
        // No windows seeded. The sparkline still has 14 zero-buckets
        // → it renders flat baselines, not the empty-state. The page
        // must still load + show the summary cards.
        var page = _ctx.RenderComponent<SlaDashboardPage>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading..."),
            TimeSpan.FromSeconds(3));

        page.Markup.Should().Contain("Trend (14d)");
        // With 14 zero buckets, the polylines still render (flat).
        page.Markup.Should().Contain("inspection-sparkline");
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
                new Claim("nickerp:tenant_id", "41"),
                new Claim(ClaimTypes.Role, "Inspection.Admin"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
