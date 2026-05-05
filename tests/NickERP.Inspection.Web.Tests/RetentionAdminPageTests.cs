using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Retention;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Components.Pages.Retention;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Features;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 44 / Phase D — bunit render coverage for the
/// <c>RetentionAdmin</c> + <c>PurgeCandidates</c> Razor pages.
/// </summary>
public sealed class RetentionAdminPageTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private readonly RetentionFakeTimeProvider _clock = new();

    public RetentionAdminPageTests()
    {
        var inspName = "ret-page-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(inspName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        _ctx.Services.AddSingleton<TimeProvider>(_clock);
        _ctx.Services.AddSingleton<IEventPublisher, NoopRetentionEventPublisher>();
        _ctx.Services.AddSingleton<ITenantSettingsService>(new InMemoryTenantSettingsService());
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        _ctx.Services.AddScoped<RetentionService>();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void RetentionAdmin_renders_empty_state()
    {
        var cut = _ctx.RenderComponent<RetentionAdmin>();
        cut.Markup.Should().Contain("Retention management");
        cut.Markup.Should().Contain("No cases are currently under a legal hold");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RetentionAdmin_renders_held_case()
    {
        // Seed a held case directly.
        using (var scope = _ctx.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(1);
            db.Cases.Add(new InspectionCase
            {
                Id = Guid.NewGuid(),
                LocationId = Guid.NewGuid(),
                SubjectType = CaseSubjectType.Container,
                SubjectIdentifier = "TEST-CON-001",
                SubjectPayloadJson = "{}",
                State = InspectionWorkflowState.Open,
                OpenedAt = _clock.UtcNow,
                StateEnteredAt = _clock.UtcNow,
                RetentionClass = RetentionClass.Standard,
                LegalHold = true,
                LegalHoldAppliedAt = _clock.UtcNow,
                LegalHoldReason = "subpoena 24-001",
                TenantId = 1
            });
            db.SaveChanges();
        }

        var cut = _ctx.RenderComponent<RetentionAdmin>();
        cut.Markup.Should().Contain("Retention management");
        cut.Markup.Should().Contain("TEST-CON-001");
        cut.Markup.Should().Contain("subpoena 24-001");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void PurgeCandidates_renders_summary_cards()
    {
        var cut = _ctx.RenderComponent<PurgeCandidates>();
        cut.Markup.Should().Contain("Purge candidates");
        cut.Markup.Should().Contain("Standard candidates");
        cut.Markup.Should().Contain("Extended candidates");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void PurgeCandidates_renders_eligible_case()
    {
        // Seed a Standard case past retention window.
        using (var scope = _ctx.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(1);
            db.Cases.Add(new InspectionCase
            {
                Id = Guid.NewGuid(),
                LocationId = Guid.NewGuid(),
                SubjectType = CaseSubjectType.Container,
                SubjectIdentifier = "OLD-CON-001",
                SubjectPayloadJson = "{}",
                State = InspectionWorkflowState.Closed,
                OpenedAt = _clock.UtcNow.AddYears(-7),
                StateEnteredAt = _clock.UtcNow.AddYears(-6),
                ClosedAt = _clock.UtcNow.AddYears(-6),
                RetentionClass = RetentionClass.Standard,
                LegalHold = false,
                TenantId = 1
            });
            db.SaveChanges();
        }

        var cut = _ctx.RenderComponent<PurgeCandidates>();
        cut.Markup.Should().Contain("OLD-CON-001");
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

    private sealed class NoopRetentionEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
            => Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
            => Task.FromResult(events);
    }
}
