using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.AnalysisServices;
using NickERP.Inspection.Application.Reviews;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Components.Pages.Reviews;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 34 / B6 Phase D — bunit coverage for the BL / AI triage /
/// Audit / MyQueue review pages. Renders each under bunit, seeds a
/// case + matching dependencies, and asserts the page's primary
/// elements are present.
/// </summary>
public sealed class ReviewPagesTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _caseId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _serviceId = Guid.NewGuid();
    private readonly long _tenantId = 1;
    private readonly DateTimeOffset _now = new(2026, 5, 5, 10, 0, 0, TimeSpan.Zero);

    public ReviewPagesTests()
    {
        // Capture DB names ONCE — the AddDbContext lambda re-runs per
        // resolution.
        var dbName = "review-pages-" + Guid.NewGuid();
        var tenancyDbName = "review-pages-tenancy-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase(tenancyDbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(_tenantId);
            return t;
        });
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider(_userId));

        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddSingleton<ISlaSettingsProvider, InMemorySlaSettingsProvider>();
        _ctx.Services.Configure<SlaTrackerOptions>(_ => { });
        _ctx.Services.AddScoped<ISlaTracker, SlaTracker>();
        _ctx.Services.AddCaseClaimAndVisibility();
        _ctx.Services.AddNickErpInspectionReviews();
        _ctx.Services.AddScoped<ReviewQueueService>();

        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _ctx.Dispose();

    private async Task SeedAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        tenancy.Tenants.Add(new Tenant
        {
            Id = _tenantId, Code = "rp", Name = "Review pages",
            CaseVisibilityModel = CaseVisibilityModel.Shared,
            AllowMultiServiceMembership = true,
        });
        await tenancy.SaveChangesAsync();

        db.Locations.Add(new Location
        {
            Id = _locationId, Code = "RP", Name = "Review pages test",
            TimeZone = "UTC", IsActive = true, TenantId = _tenantId,
        });
        db.AnalysisServices.Add(new AnalysisService
        {
            Id = _serviceId, Name = "RP service", TenantId = _tenantId,
            CreatedAt = _now, UpdatedAt = _now,
        });
        db.AnalysisServiceLocations.Add(new AnalysisServiceLocation
        {
            AnalysisServiceId = _serviceId, LocationId = _locationId,
            AddedAt = _now, TenantId = _tenantId,
        });
        db.AnalysisServiceUsers.Add(new AnalysisServiceUser
        {
            AnalysisServiceId = _serviceId, UserId = _userId,
            AssignedAt = _now, TenantId = _tenantId,
        });
        db.Cases.Add(new InspectionCase
        {
            Id = _caseId, LocationId = _locationId,
            SubjectIdentifier = "CONT-S34-PAGES",
            State = InspectionWorkflowState.Open,
            ReviewQueue = ReviewQueue.HighPriority,
            OpenedAt = _now, StateEnteredAt = _now,
            TenantId = _tenantId,
        });
        // Authority document with rich BL fields for BlReview.
        var extId = Guid.NewGuid();
        db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = extId, TypeCode = "test", DisplayName = "Test ext",
            ConfigJson = "{}", IsActive = true, TenantId = _tenantId,
            Scope = ExternalSystemBindingScope.Shared,
        });
        db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = Guid.NewGuid(), CaseId = _caseId,
            ExternalSystemInstanceId = extId,
            DocumentType = "BL", ReferenceNumber = "BL-12345",
            PayloadJson = """{"ConsigneeName":"ACME","Commodity":"Steel","Weight":"42000"}""",
            ReceivedAt = _now, TenantId = _tenantId,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void BlReview_renders_field_grid_and_action_button()
    {
        var page = _ctx.RenderComponent<BlReview>(p => p.Add(x => x.CaseId, _caseId));

        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading..."), TimeSpan.FromSeconds(3));

        page.Markup.Should().Contain("BL review");
        page.Markup.Should().Contain("CONT-S34-PAGES");
        // Field grid rendered with the seeded BL fields.
        page.Markup.Should().Contain("ConsigneeName");
        page.Markup.Should().Contain("Commodity");
        page.Markup.Should().Contain("Weight");
        page.Markup.Should().Contain("Start BL review");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void AiTriage_renders_verdict_table_or_empty_state()
    {
        var page = _ctx.RenderComponent<AiTriage>(p => p.Add(x => x.CaseId, _caseId));

        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading..."), TimeSpan.FromSeconds(3));

        page.Markup.Should().Contain("AI triage");
        page.Markup.Should().Contain("CONT-S34-PAGES");
        // No engine findings have been seeded, so the empty-state copy
        // appears. The button label still rendered.
        page.Markup.Should().Contain("Start AI triage");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void AuditReview_renders_prior_reviews_section_or_empty_state()
    {
        var page = _ctx.RenderComponent<AuditReview>(p => p.Add(x => x.CaseId, _caseId));

        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading..."), TimeSpan.FromSeconds(3));

        page.Markup.Should().Contain("Audit review");
        page.Markup.Should().Contain("CONT-S34-PAGES");
        // Empty-state copy should render — no prior reviews seeded.
        page.Markup.Should().Contain("Start audit review");
        // Decision picker rendered.
        page.Markup.Should().Contain("Concur with analyst");
        page.Markup.Should().Contain("Dissent from analyst");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MyQueue_lists_visible_case_with_priority_badge()
    {
        var page = _ctx.RenderComponent<MyQueue>();

        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading..."), TimeSpan.FromSeconds(3));

        page.Markup.Should().Contain("My review queue");
        page.Markup.Should().Contain("CONT-S34-PAGES");
        // Priority badge for HighPriority — both filter dropdown and
        // row span render the label.
        page.Markup.Should().Contain("HighPriority");
        // Row links to specialised pages.
        page.Markup.Should().Contain($"/reviews/bl/{_caseId}");
        page.Markup.Should().Contain($"/reviews/ai/{_caseId}");
        page.Markup.Should().Contain($"/reviews/audit/{_caseId}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MyQueue_user_with_no_membership_shows_empty()
    {
        // Create a fresh test context with a different user id who has
        // no AnalysisServiceUser membership.
        using var alt = new BunitTestContext();
        var stranger = Guid.NewGuid();
        alt.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase("review-pages-alt-" + Guid.NewGuid())
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        alt.Services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase("review-pages-alt-tenancy-" + Guid.NewGuid())
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        alt.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(_tenantId);
            return t;
        });
        alt.Services.AddSingleton(NullLoggerFactory.Instance);
        alt.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        alt.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider(stranger));
        alt.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        alt.Services.AddSingleton<ISlaSettingsProvider, InMemorySlaSettingsProvider>();
        alt.Services.Configure<SlaTrackerOptions>(_ => { });
        alt.Services.AddScoped<ISlaTracker, SlaTracker>();
        alt.Services.AddCaseClaimAndVisibility();
        alt.Services.AddNickErpInspectionReviews();
        alt.Services.AddScoped<ReviewQueueService>();

        // Seed an unrelated tenant row only; user has no service.
        using (var scope = alt.Services.CreateScope())
        {
            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            tenancy.Tenants.Add(new Tenant
            {
                Id = _tenantId, Code = "alt", Name = "alt",
                CaseVisibilityModel = CaseVisibilityModel.Shared,
                AllowMultiServiceMembership = true,
            });
            tenancy.SaveChanges();
        }

        var page = alt.RenderComponent<MyQueue>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading..."), TimeSpan.FromSeconds(3));

        page.Markup.Should().Contain("No cases match the current filter.");
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        private readonly Guid _userId;
        public FakeAuthStateProvider(Guid userId) => _userId = userId;
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "test"),
                new Claim("nickerp:id", _userId.ToString()),
                new Claim("nickerp:tenant_id", "1"),
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
