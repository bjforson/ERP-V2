using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Tenancy;
using NickERP.Portal.Components.Pages;
using NickERP.Portal.Services;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 56 / FU-cross-tenant-aggregation — bunit page-render coverage
/// for <see cref="AuditDashboard"/>. Asserts the three render states the
/// brief calls out:
///
/// <list type="bullet">
///   <item>empty deployment — empty-state copy renders</item>
///   <item>populated deployment — per-tenant rows + headline numbers render</item>
///   <item>all-error tenant — top-error card highlights it</item>
/// </list>
///
/// <para>
/// The service is faked via a hand-rolled
/// <see cref="StubCrossTenantAuditService"/> so the page renders
/// deterministically with whatever shape the test wants. The real
/// <see cref="CrossTenantAuditService"/> is exercised by
/// <see cref="CrossTenantAuditServiceTests"/>.
/// </para>
/// </summary>
public sealed class AuditDashboardPageTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private readonly StubCrossTenantAuditService _stub = new();

    public AuditDashboardPageTests()
    {
        _ctx.Services.AddSingleton<ICrossTenantAuditService>(_stub);
        _ctx.Services.AddSingleton<CrossTenantAuditCache>();
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void Empty_deployment_renders_no_active_tenants_copy()
    {
        // Default stub state: every method returns an empty list.
        var cut = _ctx.RenderComponent<AuditDashboard>();

        cut.Markup.Should().Contain("Audit dashboard");
        cut.Markup.Should().Contain("All-tenants summary");
        cut.Markup.Should().Contain("No active tenants in this deployment");
        cut.Markup.Should().Contain("No tenants with error events in this window");
        cut.Markup.Should().Contain("No webhook adapters configured");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Populated_deployment_renders_per_tenant_rows_and_headline_numbers()
    {
        _stub.Summary = new[]
        {
            new CrossTenantAuditSummaryRow(1, "alpha", "Alpha Co.", EventCount: 120, ErrorCount: 3, LastEventAt: DateTimeOffset.UtcNow),
            new CrossTenantAuditSummaryRow(2, "beta", "Beta Ltd.", EventCount: 47, ErrorCount: 0, LastEventAt: DateTimeOffset.UtcNow),
        };
        _stub.TopErrors = new[]
        {
            new CrossTenantAuditTopErrorRow(1, "alpha", "Alpha Co.", ErrorCount: 3, EventCount: 120, ErrorRate: 0.025d),
        };

        var cut = _ctx.RenderComponent<AuditDashboard>();

        // Aggregate headline numbers.
        cut.Markup.Should().Contain("167",  // 120 + 47 total events
            because: "headline must reflect the sum across tenants");
        cut.Markup.Should().Contain("3",
            because: "headline must surface total error events");

        // Active-tenant ratio: tenants with EventCount > 0 / total tenants.
        cut.Markup.Should().Contain("2 / 2",
            because: "both seeded tenants have events");

        // Per-tenant row drill-down link.
        cut.Markup.Should().Contain("/tenants/1");
        cut.Markup.Should().Contain("/tenants/2");
        cut.Markup.Should().Contain("Alpha Co.");
        cut.Markup.Should().Contain("Beta Ltd.");
        cut.Markup.Should().Contain("alpha");

        // Top-errors card surfaces the all-error-rate tenant.
        cut.Markup.Should().Contain("Top error tenants");
        cut.Markup.Should().Contain("2.5", because: "2.5% error rate is rendered as P1 percentage");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void All_error_tenant_highlighted_in_top_errors_card()
    {
        _stub.Summary = new[]
        {
            new CrossTenantAuditSummaryRow(1, "broken", "Broken Co.", EventCount: 5, ErrorCount: 5, LastEventAt: DateTimeOffset.UtcNow),
        };
        _stub.TopErrors = new[]
        {
            new CrossTenantAuditTopErrorRow(1, "broken", "Broken Co.", ErrorCount: 5, EventCount: 5, ErrorRate: 1.0d),
        };

        var cut = _ctx.RenderComponent<AuditDashboard>();

        cut.Markup.Should().Contain("Broken Co.");
        cut.Markup.Should().Contain("100.0", because: "100% error rate renders as a P1 percentage");
        // Active-tenant ratio still reads 1 / 1 (the broken tenant has events).
        cut.Markup.Should().Contain("1 / 1");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Window_selector_renders_all_three_choices()
    {
        var cut = _ctx.RenderComponent<AuditDashboard>();
        cut.Markup.Should().Contain(">24h<");
        cut.Markup.Should().Contain(">7d<");
        cut.Markup.Should().Contain(">30d<");
        cut.Markup.Should().Contain("Refresh now");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Webhook_health_renders_rows_when_present()
    {
        _stub.Summary = new[]
        {
            new CrossTenantAuditSummaryRow(1, "alpha", "Alpha Co.", EventCount: 10, ErrorCount: 0, LastEventAt: DateTimeOffset.UtcNow),
        };
        _stub.WebhookHealth = new[]
        {
            new CrossTenantWebhookHealthRow(
                TenantId: 1,
                TenantCode: "alpha",
                AdapterName: "siem.forwarder",
                LastProcessedEventId: Guid.NewGuid(),
                LastCursorUpdate: DateTimeOffset.UtcNow,
                LagSeconds: 12.5d),
        };

        var cut = _ctx.RenderComponent<AuditDashboard>();

        cut.Markup.Should().Contain("siem.forwarder");
        cut.Markup.Should().Contain("alpha");
        // FormatLag uses {seconds:F0} for sub-minute values which renders
        // "12" or "13" depending on banker's rounding. Either is fine —
        // assert on the seconds-bucket suffix.
        cut.Markup.Should().MatchRegex(@">\s*1[23]s\s*<",
            because: "12.5 seconds renders as 12s or 13s depending on rounding");
    }

    /// <summary>
    /// Hand-rolled fake — the production
    /// <see cref="CrossTenantAuditService"/> is covered by its own
    /// service-level tests; the page test only needs to render output
    /// deterministically per shape.
    /// </summary>
    private sealed class StubCrossTenantAuditService : ICrossTenantAuditService
    {
        public IReadOnlyList<CrossTenantAuditSummaryRow> Summary { get; set; }
            = Array.Empty<CrossTenantAuditSummaryRow>();
        public IReadOnlyList<CrossTenantAuditTopErrorRow> TopErrors { get; set; }
            = Array.Empty<CrossTenantAuditTopErrorRow>();
        public IReadOnlyList<CrossTenantWebhookHealthRow> WebhookHealth { get; set; }
            = Array.Empty<CrossTenantWebhookHealthRow>();
        public TenantAuditActivity? Activity { get; set; }

        public Task<IReadOnlyList<CrossTenantAuditSummaryRow>> GetCrossTenantSummaryAsync(
            TimeSpan window, CancellationToken ct = default)
            => Task.FromResult(Summary);

        public Task<IReadOnlyList<CrossTenantAuditTopErrorRow>> GetCrossTenantTopErrorsAsync(
            TimeSpan window, int take, CancellationToken ct = default)
            => Task.FromResult(TopErrors);

        public Task<IReadOnlyList<CrossTenantWebhookHealthRow>> GetCrossTenantWebhookHealthAsync(
            CancellationToken ct = default)
            => Task.FromResult(WebhookHealth);

        public Task<TenantAuditActivity> GetTenantAuditActivityAsync(
            long tenantId, TimeSpan window, int take,
            TenantAuditActivityFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult(Activity ?? new TenantAuditActivity(tenantId, 0, Array.Empty<TenantAuditActivityRow>()));
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "admin"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
                new Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
