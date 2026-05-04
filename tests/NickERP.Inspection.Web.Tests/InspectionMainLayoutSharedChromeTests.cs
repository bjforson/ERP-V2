using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Inspection.Web.Components.Layout;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Web.Shared.Modules;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 29 — verifies <see cref="MainLayout"/> in Inspection.Web
/// adopts the cross-module shared chrome (SharedHeader + SharedFooter)
/// without breaking the legacy TopNav + sidenav arrangement. Renders
/// the layout under bunit and asserts both the new chrome markers and
/// the existing inspection-shell markers are present.
/// </summary>
public sealed class InspectionMainLayoutSharedChromeTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();

    public InspectionMainLayoutSharedChromeTests()
    {
        _ctx.Services.AddNickErpSharedChrome(opts =>
        {
            opts.ModuleId = "inspection";
            opts.DisplayName = "Inspection v2";
            opts.PortalLauncherUrl = "https://portal.test/";
        });
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        // The legacy TopNav slot inside MainLayout instantiates the
        // notifications bell, which @inject's AuditDbContext + tenant
        // context. AuditDbContext.DomainEventRow.Payload is a
        // JsonDocument that the EF in-memory provider can't natively
        // map, so we use the same TestAuditDbContext-style subclass
        // pattern as AuditNotificationProjectorTests.
        _ctx.Services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase("audit-bunit-" + Guid.NewGuid())
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddScoped<AuditDbContext>(sp =>
            new TestAuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()));
        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
    }

    /// <summary>
    /// Test-only subclass that adds a JsonDocument↔string value converter
    /// on <c>DomainEventRow.Payload</c> so the EF in-memory provider can
    /// materialise the column. Mirrors the pattern in
    /// <c>AuditNotificationProjectorTests.TestAuditDbContext</c>; needed
    /// because the legacy NotificationsBell @inject's AuditDbContext.
    /// </summary>
    private sealed class TestAuditDbContext : AuditDbContext
    {
        public TestAuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var converter = new ValueConverter<JsonDocument, string>(
                v => v.RootElement.GetRawText(),
                v => JsonDocument.Parse(v, default));
            modelBuilder.Entity<DomainEventRow>()
                .Property(e => e.Payload)
                .HasConversion(converter);
        }
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void MainLayout_RendersSharedHeaderAndFooter_AlongsideLegacySidenav()
    {
        var layout = _ctx.RenderComponent<MainLayout>();

        // Sprint 29 chrome markers — the SharedHeader/Footer's distinctive
        // class hooks are present.
        layout.Markup.Should().Contain("nickerp-shared-header");
        layout.Markup.Should().Contain("nickerp-shared-footer");
        layout.Markup.Should().Contain("https://portal.test/");
        layout.Markup.Should().Contain("Inspection v2");

        // Legacy markers we must not regress: the inspection-shell
        // wrapper + sidenav navigation links.
        layout.Markup.Should().Contain("inspection-shell");
        layout.Markup.Should().Contain("inspection-sidenav");
        layout.Markup.Should().Contain("href=\"/cases\"");
        layout.Markup.Should().Contain("href=\"/admin/analysis-services\"");
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "Test User"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
                new Claim("nickerp:display_name", "Test User"),
                new Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
