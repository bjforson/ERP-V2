using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
/// Sprint 46 / Phase C — bunit render coverage for the
/// <c>/admin/reviews/throughput</c> page (Sprint 42 FU-reviews-
/// throughput-page deferred test). Renders the page in two states:
///
/// <list type="bullet">
///   <item><description>Empty: no AnalystReview rows seeded — the page
///   should still render its time-window selector + the per-type and
///   per-queue empty-state copy without throwing.</description></item>
///   <item><description>Populated: a mix of human-emitted (Standard /
///   BlReview) + engine-emitted (EngineValidation / EngineCompleteness)
///   reviews — the page surfaces all four summary cards + the
///   per-ReviewType breakdown + the per-ReviewQueue priority breakdown
///   with row counts.</description></item>
/// </list>
///
/// <para>
/// Authz: the page's <c>[Authorize(Roles = "Inspection.RulesAdmin,
/// Inspection.Admin")]</c> attribute is honoured on the running host;
/// the bunit test bypasses authz by rendering the component directly,
/// matching the rest of the admin-page tests.
/// </para>
/// </summary>
public sealed class ThroughputReportsPageTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private readonly long _tenantId = 1;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _serviceId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddMinutes(-5);

    public ThroughputReportsPageTests()
    {
        var dbName = "throughput-page-" + Guid.NewGuid();
        var tenancyDbName = "throughput-page-tenancy-" + Guid.NewGuid();
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
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(
            new FakeAuthStateProvider(_userId));

        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddSingleton<ISlaSettingsProvider, InMemorySlaSettingsProvider>();
        _ctx.Services.Configure<SlaTrackerOptions>(_ => { });
        _ctx.Services.AddScoped<ISlaTracker, SlaTracker>();
        _ctx.Services.AddCaseClaimAndVisibility();
        _ctx.Services.AddNickErpInspectionReviews();
        _ctx.Services.AddScoped<ReviewQueueService>();
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>
    /// Seed the minimum tenancy + service / location / membership rows
    /// the page implicitly depends on through ReviewQueueService — even
    /// the empty-state path resolves the ITenantContext + service.
    /// </summary>
    private async Task SeedTenancyAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        tenancy.Tenants.Add(new Tenant
        {
            Id = _tenantId, Code = "tp", Name = "Throughput page",
            CaseVisibilityModel = CaseVisibilityModel.Shared,
            AllowMultiServiceMembership = true,
        });
        await tenancy.SaveChangesAsync();

        db.Locations.Add(new Location
        {
            Id = _locationId, Code = "TP", Name = "Throughput page test",
            TimeZone = "UTC", IsActive = true, TenantId = _tenantId,
        });
        db.AnalysisServices.Add(new AnalysisService
        {
            Id = _serviceId, Name = "TP service", TenantId = _tenantId,
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
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seed two cases (one Standard queue, one HighPriority) plus four
    /// AnalystReview rows: Standard / BlReview / EngineValidation /
    /// EngineCompleteness, with mixed completed / in-progress states so
    /// the populated-data assertions have material to land on.
    /// </summary>
    private async Task SeedReviewsAsync()
    {
        using var scope = _ctx.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var caseStandard = new InspectionCase
        {
            Id = Guid.NewGuid(), LocationId = _locationId,
            SubjectIdentifier = "TP-CASE-A",
            State = InspectionWorkflowState.Open,
            ReviewQueue = ReviewQueue.Standard,
            OpenedAt = _now, StateEnteredAt = _now,
            TenantId = _tenantId,
        };
        var caseHighPriority = new InspectionCase
        {
            Id = Guid.NewGuid(), LocationId = _locationId,
            SubjectIdentifier = "TP-CASE-B",
            State = InspectionWorkflowState.Open,
            ReviewQueue = ReviewQueue.HighPriority,
            OpenedAt = _now, StateEnteredAt = _now,
            TenantId = _tenantId,
        };
        db.Cases.AddRange(caseStandard, caseHighPriority);

        var session = new ReviewSession
        {
            Id = Guid.NewGuid(),
            CaseId = caseStandard.Id,
            AnalystUserId = _userId,
            StartedAt = _now,
            Outcome = "in-progress",
            TenantId = _tenantId,
        };
        db.ReviewSessions.Add(session);

        // Standard human-emitted, completed
        db.AnalystReviews.Add(new AnalystReview
        {
            Id = Guid.NewGuid(),
            ReviewSessionId = session.Id,
            ReviewType = ReviewType.Standard,
            StartedByUserId = _userId,
            ConfidenceScore = 0.9,
            CreatedAt = _now,
            CompletedAt = _now.AddMinutes(2),
            TimeToDecisionMs = 120_000,
            Outcome = "completed",
            TenantId = _tenantId,
        });
        // BlReview human-emitted, in-progress
        db.AnalystReviews.Add(new AnalystReview
        {
            Id = Guid.NewGuid(),
            ReviewSessionId = session.Id,
            ReviewType = ReviewType.BlReview,
            StartedByUserId = _userId,
            ConfidenceScore = 0.5,
            CreatedAt = _now,
            TenantId = _tenantId,
        });
        // EngineValidation engine-emitted, completed
        db.AnalystReviews.Add(new AnalystReview
        {
            Id = Guid.NewGuid(),
            ReviewSessionId = session.Id,
            ReviewType = ReviewType.EngineValidation,
            ConfidenceScore = 1.0,
            CreatedAt = _now,
            CompletedAt = _now,
            TimeToDecisionMs = 0,
            Outcome = "completed",
            TenantId = _tenantId,
        });
        // EngineCompleteness engine-emitted, completed
        db.AnalystReviews.Add(new AnalystReview
        {
            Id = Guid.NewGuid(),
            ReviewSessionId = session.Id,
            ReviewType = ReviewType.EngineCompleteness,
            ConfidenceScore = 1.0,
            CreatedAt = _now,
            CompletedAt = _now,
            TimeToDecisionMs = 0,
            Outcome = "completed",
            TenantId = _tenantId,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThroughputReports_renders_empty_state_when_no_data()
    {
        await SeedTenancyAsync();

        var page = _ctx.RenderComponent<ThroughputReports>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading…"),
            TimeSpan.FromSeconds(3));

        // Header rendered.
        page.Markup.Should().Contain("Review throughput");
        // Time-window selector buttons are present.
        page.Markup.Should().Contain("24h");
        page.Markup.Should().Contain("7d");
        page.Markup.Should().Contain("30d");
        // The four summary cards still render with empty headlines.
        page.Markup.Should().Contain("Total reviews");
        page.Markup.Should().Contain("Active reviewers");
        page.Markup.Should().Contain("Avg time-to-decision");
        page.Markup.Should().Contain("Escalations");
        // Per-type empty-state copy.
        page.Markup.Should().Contain("No reviews in this window.");
        // Per-queue empty-state copy.
        page.Markup.Should().Contain("No cases opened in this window.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThroughputReports_renders_summary_cards_and_breakdowns_when_data()
    {
        await SeedTenancyAsync();
        await SeedReviewsAsync();

        var page = _ctx.RenderComponent<ThroughputReports>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading…"),
            TimeSpan.FromSeconds(3));

        // Summary card titles.
        page.Markup.Should().Contain("Total reviews");
        page.Markup.Should().Contain("Active reviewers");
        page.Markup.Should().Contain("Avg time-to-decision");
        page.Markup.Should().Contain("Escalations");

        // Per-ReviewType breakdown — both human + engine types appear.
        page.Markup.Should().Contain("Per-review-type breakdown");
        page.Markup.Should().Contain("Standard");
        page.Markup.Should().Contain("BlReview");
        page.Markup.Should().Contain("EngineValidation");
        page.Markup.Should().Contain("EngineCompleteness");
        // Engine rows are badged.
        page.Markup.Should().Contain("engine");

        // Per-ReviewQueue breakdown — both seeded queues appear.
        page.Markup.Should().Contain("Per-queue priority breakdown");
        page.Markup.Should().Contain("Standard");
        page.Markup.Should().Contain("HighPriority");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThroughputReports_human_engine_split_visible_on_avg_card()
    {
        await SeedTenancyAsync();
        await SeedReviewsAsync();

        var page = _ctx.RenderComponent<ThroughputReports>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading…"),
            TimeSpan.FromSeconds(3));

        // The avg-TTD card surfaces the human-vs-engine completed split
        // so an admin can tell why the engine bucket pulls the average
        // down. Both rows present.
        page.Markup.Should().Contain("Human-emitted (completed)");
        page.Markup.Should().Contain("Engine-emitted (completed)");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThroughputReports_does_not_throw_when_tenant_unresolved()
    {
        // No SeedTenancyAsync — tenant context is set to 1, but the
        // page rendering shouldn't throw if no AnalystReviews exist for
        // tenant 1. This is the regression we're guarding against —
        // the page must not throw on first-render with an unseeded DB.
        var page = _ctx.RenderComponent<ThroughputReports>();
        page.WaitForAssertion(() => page.Markup.Should().NotContain("Loading…"),
            TimeSpan.FromSeconds(3));
        page.Markup.Should().Contain("Review throughput");
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
