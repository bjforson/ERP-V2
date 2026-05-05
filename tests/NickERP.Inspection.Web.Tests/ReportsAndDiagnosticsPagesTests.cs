using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Components.Pages.Diagnostics;
using NickERP.Inspection.Web.Components.Pages.Reports;
using NickERP.Inspection.Web.Components.Shared;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Telemetry;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 33 / B7 — Razor SSR smoke tests for the new
/// reports + diagnostics pages. Asserts that each page renders
/// without throwing against in-memory fixtures and surfaces the
/// expected sentinel content (heading text, follow-up notes for
/// the Sprint 31 race / Seq-not-configured paths).
///
/// <para>
/// Mirrors the <c>RazorFormBindingTests</c> pattern: bunit
/// TestContext + EF in-memory + a fake AuthenticationStateProvider.
/// Docker is unavailable so we don't go anywhere near a real
/// HealthCheckService here — the diagnostics health page simply
/// renders the "no HealthCheckService" path on these tests.
/// </para>
/// </summary>
public sealed class ReportsAndDiagnosticsPagesTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();

    public ReportsAndDiagnosticsPagesTests()
    {
        var insp = "reports-pages-insp-" + Guid.NewGuid();
        var aud = "reports-pages-aud-" + Guid.NewGuid();

        _ctx.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(insp)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddDbContext<InMemoryAuditDb>(o =>
            o.UseInMemoryDatabase(aud)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddScoped<AuditDbContext>(sp => sp.GetRequiredService<InMemoryAuditDb>());

        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });

        _ctx.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        _ctx.Services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build());
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.Services.AddScoped<ReportsService>();
        _ctx.Services.AddScoped<DiagnosticsService>();

        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void Reports_Dashboard_renders_with_all_four_cards()
    {
        var page = _ctx.RenderComponent<Dashboard>();
        var markup = page.Markup;
        markup.Should().Contain("Reports");
        markup.Should().Contain("Throughput");
        markup.Should().Contain("SLA windows");
        markup.Should().Contain("Errors");
        markup.Should().Contain("Audit activity");
        // Sprint 31 has shipped — SLA card now reads against the
        // typed SlaWindow DbSet. Empty fixture surfaces the zero-state
        // version of the live card rather than the "Pending" placeholder.
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Reports_Throughput_renders_empty_table_with_no_data()
    {
        var page = _ctx.RenderComponent<Throughput>();
        var markup = page.Markup;
        markup.Should().Contain("Throughput drill-down");
        markup.Should().Contain("Per-day breakdown");
        // Empty fixture — the empty-state hint should appear.
        markup.Should().Contain("No cases created");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Reports_Errors_renders_empty_state_when_no_audit_rows()
    {
        var page = _ctx.RenderComponent<Errors>();
        var markup = page.Markup;
        markup.Should().Contain("Error investigation");
        markup.Should().Contain("Filters");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Reports_AuditEvents_renders_empty_state_when_no_audit_rows()
    {
        var page = _ctx.RenderComponent<AuditEvents>();
        var markup = page.Markup;
        markup.Should().Contain("Audit-event browser");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Diagnostics_Health_renders_with_no_HealthCheckService()
    {
        var page = _ctx.RenderComponent<Health>();
        var markup = page.Markup;
        markup.Should().Contain("Diagnostics — health");
        // No HealthCheckService registered → Overall reads "Unknown".
        markup.Should().Contain("Unknown");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Diagnostics_Workers_renders_with_no_probes()
    {
        var page = _ctx.RenderComponent<Workers>();
        var markup = page.Markup;
        markup.Should().Contain("Diagnostics — workers");
        markup.Should().Contain("No background workers");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Diagnostics_Logs_surfaces_NotConfigured_placeholder()
    {
        var page = _ctx.RenderComponent<Logs>();
        var markup = page.Markup;
        markup.Should().Contain("Diagnostics — logs");
        markup.Should().Contain("Seq is not configured");
        markup.Should().Contain("FU-inline-log-viewer");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Diagnostics_Logs_renders_link_when_SeqUiUrl_configured()
    {
        // Rebuild a context with the SeqUiUrl set.
        using var ctx = new BunitTestContext();
        ctx.Services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [DiagnosticsService.SeqUiUrlConfigKey] = "https://logs.example.com/",
                })
                .Build());
        ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        ctx.Services.AddScoped<DiagnosticsService>();
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        var page = ctx.RenderComponent<Logs>();
        var markup = page.Markup;
        markup.Should().Contain("https://logs.example.com/");
        markup.Should().Contain("Open Seq");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReportSummaryCard_renders_title_and_drill_link()
    {
        using var ctx = new BunitTestContext();
        var rows = new List<ReportSummaryRow>
        {
            new("Last 24h", "47"),
            new("Last 7d", "312"),
        };
        var card = ctx.RenderComponent<ReportSummaryCard>(parameters => parameters
            .Add(p => p.Title, "Throughput")
            .Add(p => p.Headline, "47")
            .Add(p => p.HeadlineSubtitle, "in the last 24h")
            .Add(p => p.Rows, rows)
            .Add(p => p.DetailHref, "/admin/reports/throughput")
            .Add(p => p.BadgeText, "Live")
            .Add(p => p.BadgeKind, "success"));

        var markup = card.Markup;
        markup.Should().Contain("Throughput");
        markup.Should().Contain("47");
        markup.Should().Contain("in the last 24h");
        markup.Should().Contain("/admin/reports/throughput");
        markup.Should().Contain("Last 24h");
        markup.Should().Contain("312");
        markup.Should().Contain("Live");
    }

    // -----------------------------------------------------------------

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test-admin"),
                new Claim(ClaimTypes.Role, "Inspection.Admin"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
                new Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    /// <summary>
    /// In-memory subclass of <see cref="AuditDbContext"/> with the
    /// JsonDocument↔string converter required by the EF in-memory
    /// provider. Same pattern as
    /// <c>CaseDetailTabsTests.InMemoryAuditDbContext</c>.
    /// </summary>
    private sealed class InMemoryAuditDb : AuditDbContext
    {
        public InMemoryAuditDb(DbContextOptions<InMemoryAuditDb> options)
            : base(BuildBaseOptions(options)) { }

        private static DbContextOptions<AuditDbContext> BuildBaseOptions(
            DbContextOptions<InMemoryAuditDb> source)
        {
            var b = new DbContextOptionsBuilder<AuditDbContext>();
            foreach (var e in source.Extensions)
                ((Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsBuilderInfrastructure)b)
                    .AddOrUpdateExtension(e);
            return b.Options;
        }
        protected override void OnAuditModelCreating(ModelBuilder modelBuilder)
        {
            base.OnAuditModelCreating(modelBuilder);
            var conv = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<System.Text.Json.JsonDocument, string>(
                v => v.RootElement.GetRawText(),
                v => System.Text.Json.JsonDocument.Parse(v, default));
            modelBuilder.Entity<DomainEventRow>()
                .Property(e => e.Payload)
                .HasConversion(conv);
        }
    }
}
