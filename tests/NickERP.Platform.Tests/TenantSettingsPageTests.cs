using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Platform.Tenancy.Features;
using NickERP.Portal.Components.Pages;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 35 / B8.2 — bunit page-render coverage for
/// <see cref="TenantSettings"/>. Asserts the page renders the
/// catalogue dropdown groupings (Inspection / Comms gateway / Portal)
/// + the persisted-settings table when rows exist.
/// </summary>
public sealed class TenantSettingsPageTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();

    public TenantSettingsPageTests()
    {
        var dbName = "tenant-settings-page-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });

        _ctx.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddScoped<ITenantSettingsService, TenantSettingsService>();
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void TenantSettings_ShowsEmptyState_AndCatalogueGroups()
    {
        var cut = _ctx.RenderComponent<TenantSettings>();

        cut.Markup.Should().Contain("Tenant settings");
        cut.Markup.Should().Contain("No settings persisted for this tenant");

        // optgroup labels rendered.
        cut.Markup.Should().Contain("Inspection");
        cut.Markup.Should().Contain("Comms gateway");
        cut.Markup.Should().Contain("Portal");

        // Sample known keys from each group.
        cut.Markup.Should().Contain("inspection.sla.default_budget_minutes");
        cut.Markup.Should().Contain("comms.email.smtp_host");
        cut.Markup.Should().Contain("portal.support.contact_email");

        // Comms-gateway runbook reference is in the page subtitle.
        cut.Markup.Should().Contain("docs/runbooks/12-comms-gateway-settings.md");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void TenantSettings_RendersPersistedRow()
    {
        using (var scope = _ctx.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            db.TenantSettings.Add(new TenantSetting
            {
                Id = Guid.NewGuid(),
                TenantId = 1,
                SettingKey = "comms.email.smtp_host",
                Value = "smtp.example.com",
                UpdatedAt = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero),
            });
            db.SaveChanges();
        }

        var cut = _ctx.RenderComponent<TenantSettings>();

        cut.Markup.Should().Contain("comms.email.smtp_host");
        cut.Markup.Should().Contain("smtp.example.com");
        cut.Markup.Should().Contain("Edit");
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

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) => Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
            => Task.FromResult(events);
    }
}
