using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Services;
using ScannersPage = NickERP.Inspection.Web.Components.Pages.Scanners;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 46 / Phase D — bunit render coverage for the Scanner-page
/// wizard UI extension added in Phase A. Asserts:
///
/// <list type="bullet">
///   <item><description>The base /scanners page still renders (the
///   wizard extension didn't break the existing register-scanner
///   form).</description></item>
///   <item><description>The "Onboard new scanner" wizard section
///   renders with the start form.</description></item>
///   <item><description>An empty-DB render doesn't throw.</description></item>
/// </list>
///
/// <para>
/// Wizard interaction (start → fill → save) isn't simulated — bunit's
/// SSR-only render is enough to guard against the obvious "page won't
/// load" regressions; the deeper interaction is covered by the service-
/// level <see cref="ScannerOnboardingServiceTests"/>.
/// </para>
/// </summary>
public sealed class ScannersPageWizardTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private const long TenantId = 1L;

    public ScannersPageWizardTests()
    {
        var dbName = "scanners-page-" + Guid.NewGuid();
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
        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddSingleton<IPluginRegistry>(
            new PluginRegistry(Array.Empty<RegisteredPlugin>()));

        _ctx.Services.AddScoped<ScannerOnboardingService>();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void Scanners_page_renders_register_form_and_wizard_section()
    {
        var page = _ctx.RenderComponent<ScannersPage>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading…"),
            TimeSpan.FromSeconds(3));

        // Base register-scanner form is preserved.
        page.Markup.Should().Contain("Scanner devices");
        page.Markup.Should().Contain("Register scanner");

        // Phase A wizard — start form section renders.
        page.Markup.Should().Contain("Onboard new scanner");
        page.Markup.Should().Contain("Vendor-survey questionnaire");
        page.Markup.Should().Contain("Scanner type code");
        page.Markup.Should().Contain("Start questionnaire");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Scanners_page_renders_with_no_scanners_in_db()
    {
        var page = _ctx.RenderComponent<ScannersPage>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading…"),
            TimeSpan.FromSeconds(3));
        // The empty-state text from the existing list section.
        page.Markup.Should().Contain("No scanners registered yet.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Scanners_page_does_not_show_questionnaire_steps_initially()
    {
        // The wizard steps (12 questions) are gated behind the
        // _wizardActive flag — they render only after the operator
        // submits the start form. On first render, the per-question
        // textareas should not be present.
        var page = _ctx.RenderComponent<ScannersPage>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading…"),
            TimeSpan.FromSeconds(3));

        // Specific per-question label text from Annex B Table 55 —
        // these only render once _wizardActive is true.
        page.Markup.Should().NotContain("Manufacturer / model / firmware");
        page.Markup.Should().NotContain("Save + mark complete");
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
                new Claim("nickerp:tenant_id", "1"),
                new Claim(ClaimTypes.Role, "Inspection.Admin"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) =>
            Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default) =>
            Task.FromResult(events);
    }
}
