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
/// <see cref="FeatureFlags"/>. Asserts the page renders the catalogue
/// dropdown + the persisted-flags table when rows exist and surfaces
/// the empty-state copy when there are none.
/// </summary>
public sealed class FeatureFlagsPageTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();

    public FeatureFlagsPageTests()
    {
        var dbName = "feature-flags-page-" + Guid.NewGuid();
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
        _ctx.Services.AddScoped<IFeatureFlagService, FeatureFlagService>();
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void FeatureFlags_ShowsEmptyState_WhenNoRows()
    {
        var cut = _ctx.RenderComponent<FeatureFlags>();
        cut.Markup.Should().Contain("Feature flags");
        cut.Markup.Should().Contain("No flags persisted for this tenant");
        // Catalogue keys are visible regardless of persisted rows.
        cut.Markup.Should().Contain("inspection.cross_record_split.auto_resolve");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void FeatureFlags_RendersPersistedRow()
    {
        using (var scope = _ctx.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            db.FeatureFlags.Add(new FeatureFlag
            {
                Id = Guid.NewGuid(),
                TenantId = 1,
                FlagKey = "portal.test.flag",
                Enabled = true,
                UpdatedAt = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero),
            });
            db.SaveChanges();
        }

        var cut = _ctx.RenderComponent<FeatureFlags>();

        cut.Markup.Should().Contain("portal.test.flag");
        cut.Markup.Should().Contain("Enabled");
        // The toggle button always offers the opposite state.
        cut.Markup.Should().Contain("Disable");
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
