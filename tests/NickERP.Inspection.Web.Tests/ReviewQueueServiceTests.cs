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
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 34 / B6 Phase D — coverage for the
/// <see cref="ReviewQueueService"/>: queue listing under VP6 visibility,
/// first-claim-wins claim semantics, throughput rollup. Builds the
/// service against an in-memory InspectionDbContext + TenancyDbContext;
/// the underlying CaseClaimService + CaseVisibilityService come from
/// AddCaseClaimAndVisibility.
/// </summary>
public sealed class ReviewQueueServiceTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;

    public ReviewQueueServiceTests()
    {
        // Capture DB names ONCE — the AddDbContext lambda re-runs per
        // resolution, so embedding Guid.NewGuid() inline would give
        // each scope its own private DB. Same-name keeps the in-memory
        // store consistent across scopes.
        var inspectionDbName = "review-queue-" + Guid.NewGuid();
        var tenancyDbName = "review-queue-tenancy-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(inspectionDbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase(tenancyDbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddCaseClaimAndVisibility();
        services.AddNickErpInspectionReviews();
        services.AddSingleton<IEventPublisher, RecordingEventPublisher>();
        services.AddSingleton<ISlaSettingsProvider, InMemorySlaSettingsProvider>();
        services.Configure<SlaTrackerOptions>(_ => { });
        services.AddScoped<ISlaTracker, SlaTracker>();
        services.AddScoped<ReviewQueueService>();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    /// <summary>
    /// Seed a tenant row + a service + a location bound to the
    /// service + the user as a member. Returns the
    /// (analysisServiceId, locationId, userId) tuple.
    /// </summary>
    private async Task<(Guid svc, Guid loc, Guid user)> SeedTenancyAsync(
        CaseVisibilityModel visibility = CaseVisibilityModel.Shared)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenancyDb = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        tenancyDb.Tenants.Add(new Tenant
        {
            Id = _tenantId,
            Code = "rq-test",
            Name = "Review queue test",
            CaseVisibilityModel = visibility,
            AllowMultiServiceMembership = true,
        });
        await tenancyDb.SaveChangesAsync();

        var loc = Guid.NewGuid();
        db.Locations.Add(new Location
        {
            Id = loc, Code = "RQ", Name = "Review-queue test",
            TimeZone = "UTC", IsActive = true, TenantId = _tenantId,
        });
        var svc = Guid.NewGuid();
        db.AnalysisServices.Add(new AnalysisService
        {
            Id = svc, Name = "rq-service", TenantId = _tenantId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.AnalysisServiceLocations.Add(new AnalysisServiceLocation
        {
            AnalysisServiceId = svc, LocationId = loc,
            AddedAt = DateTimeOffset.UtcNow, TenantId = _tenantId,
        });
        var user = Guid.NewGuid();
        db.AnalysisServiceUsers.Add(new AnalysisServiceUser
        {
            AnalysisServiceId = svc, UserId = user,
            AssignedAt = DateTimeOffset.UtcNow, TenantId = _tenantId,
        });
        await db.SaveChangesAsync();
        return (svc, loc, user);
    }

    private async Task<Guid> SeedCaseAsync(
        Guid locationId,
        ReviewQueue queue = ReviewQueue.Standard,
        InspectionWorkflowState state = InspectionWorkflowState.Open)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId, LocationId = locationId,
            SubjectIdentifier = "RQ-" + caseId.ToString("N")[..6],
            State = state, ReviewQueue = queue,
            OpenedAt = DateTimeOffset.UtcNow, StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId,
        });
        await db.SaveChangesAsync();
        return caseId;
    }

    [Fact]
    public async Task GetMyQueue_returns_only_visible_cases()
    {
        var (svc, loc, user) = await SeedTenancyAsync();
        var visibleCase = await SeedCaseAsync(loc);
        // Seed a case at a different location (not visible).
        var foreignLoc = Guid.NewGuid();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            db.Locations.Add(new Location
            {
                Id = foreignLoc, Code = "FOR", Name = "foreign",
                TimeZone = "UTC", IsActive = true, TenantId = _tenantId,
            });
            await db.SaveChangesAsync();
        }
        await SeedCaseAsync(foreignLoc);

        using var s = _sp.CreateScope();
        var queue = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        var rows = await queue.GetMyQueueAsync(user);

        rows.Should().HaveCount(1);
        rows[0].CaseId.Should().Be(visibleCase);
    }

    [Fact]
    public async Task GetMyQueue_orders_high_priority_first()
    {
        var (svc, loc, user) = await SeedTenancyAsync();
        var standard = await SeedCaseAsync(loc, ReviewQueue.Standard);
        var urgent = await SeedCaseAsync(loc, ReviewQueue.Urgent);
        var high = await SeedCaseAsync(loc, ReviewQueue.HighPriority);

        using var s = _sp.CreateScope();
        var queue = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        var rows = await queue.GetMyQueueAsync(user);

        rows.Should().HaveCount(3);
        // Order: Urgent (20), HighPriority (10), Standard (0).
        rows[0].CaseId.Should().Be(urgent);
        rows[1].CaseId.Should().Be(high);
        rows[2].CaseId.Should().Be(standard);
    }

    [Fact]
    public async Task GetMyQueue_priority_filter_narrows_to_one_bucket()
    {
        var (svc, loc, user) = await SeedTenancyAsync();
        await SeedCaseAsync(loc, ReviewQueue.Standard);
        var urgent = await SeedCaseAsync(loc, ReviewQueue.Urgent);

        using var s = _sp.CreateScope();
        var queue = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        var rows = await queue.GetMyQueueAsync(user, queueFilter: ReviewQueue.Urgent);

        rows.Should().ContainSingle(r => r.CaseId == urgent);
    }

    [Fact]
    public async Task GetMyQueue_surfaces_active_claim_when_other_user_holds_it()
    {
        var (svc, loc, user) = await SeedTenancyAsync();
        var caseId = await SeedCaseAsync(loc);
        var other = Guid.NewGuid();

        // Other user claims first.
        using (var scope = _sp.CreateScope())
        {
            var claims = scope.ServiceProvider.GetRequiredService<CaseClaimService>();
            await claims.AcquireClaimAsync(caseId, svc, other);
        }

        using var s = _sp.CreateScope();
        var queue = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        var rows = await queue.GetMyQueueAsync(user);

        var row = rows.Should().ContainSingle().Subject;
        row.ActiveClaim.Should().NotBeNull();
        row.ActiveClaim!.ClaimedByUserId.Should().Be(other);
    }

    [Fact]
    public async Task ClaimReview_acquires_claim_and_starts_review()
    {
        var (svc, loc, user) = await SeedTenancyAsync();
        var caseId = await SeedCaseAsync(loc);

        using var s = _sp.CreateScope();
        var queue = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        var reviewId = await queue.ClaimReviewAsync(caseId, svc, ReviewType.BlReview, user);

        var db = s.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var review = await db.AnalystReviews.AsNoTracking().SingleAsync(r => r.Id == reviewId);
        review.ReviewType.Should().Be(ReviewType.BlReview);
        review.StartedByUserId.Should().Be(user);

        var claims = s.ServiceProvider.GetRequiredService<CaseClaimService>();
        var active = await claims.GetActiveClaimAsync(caseId);
        active.Should().NotBeNull();
        active!.ClaimedByUserId.Should().Be(user);
    }

    [Fact]
    public async Task ClaimReview_loser_gets_CaseAlreadyClaimedException()
    {
        var (svc, loc, _) = await SeedTenancyAsync();
        var caseId = await SeedCaseAsync(loc);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        using (var scope = _sp.CreateScope())
        {
            var queueA = scope.ServiceProvider.GetRequiredService<ReviewQueueService>();
            await queueA.ClaimReviewAsync(caseId, svc, ReviewType.BlReview, userA);
        }

        using var s = _sp.CreateScope();
        var queueB = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        var act = async () =>
            await queueB.ClaimReviewAsync(caseId, svc, ReviewType.BlReview, userB);

        await act.Should().ThrowAsync<CaseAlreadyClaimedException>();
    }

    [Fact]
    public async Task ClaimReview_same_user_idempotent_returns_new_review_id()
    {
        var (svc, loc, user) = await SeedTenancyAsync();
        var caseId = await SeedCaseAsync(loc);

        Guid first;
        using (var scope = _sp.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<ReviewQueueService>();
            first = await queue.ClaimReviewAsync(caseId, svc, ReviewType.BlReview, user);
        }

        using var s = _sp.CreateScope();
        var queue2 = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        // Same user re-claiming is idempotent at the claim layer; the
        // typed AnalystReview is still a fresh row (different review
        // sessions can stack on the same case under the same user).
        var second = await queue2.ClaimReviewAsync(caseId, svc, ReviewType.AiTriage, user);
        second.Should().NotBe(first);

        var db = s.ServiceProvider.GetRequiredService<InspectionDbContext>();
        (await db.AnalystReviews.AsNoTracking().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task EscalateReview_passes_through_to_workflow()
    {
        var (svc, loc, user) = await SeedTenancyAsync();
        var caseId = await SeedCaseAsync(loc);
        var supervisor = Guid.NewGuid();

        Guid reviewId;
        using (var scope = _sp.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<ReviewQueueService>();
            reviewId = await queue.ClaimReviewAsync(caseId, svc, ReviewType.AuditReview, user);
        }

        using var s = _sp.CreateScope();
        var queue2 = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        await queue2.EscalateReviewAsync(reviewId, user, supervisor, "needs second eye");

        var db = s.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var review = await db.AnalystReviews.AsNoTracking().SingleAsync();
        review.StartedByUserId.Should().Be(supervisor);
    }

    [Fact]
    public async Task GetThroughput_groups_by_review_type()
    {
        var (svc, loc, user) = await SeedTenancyAsync();

        // Seed two BL reviews + one AI triage review on different cases.
        using (var scope = _sp.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<ReviewQueueService>();
            var c1 = await SeedCaseAsync(loc);
            var c2 = await SeedCaseAsync(loc);
            var c3 = await SeedCaseAsync(loc);
            var r1 = await queue.ClaimReviewAsync(c1, svc, ReviewType.BlReview, user);
            var r2 = await queue.ClaimReviewAsync(c2, svc, ReviewType.BlReview, user);
            var r3 = await queue.ClaimReviewAsync(c3, svc, ReviewType.AiTriage, user);

            var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflow>();
            await workflow.CompleteReviewAsync(r1, "completed", new List<Finding>(), user);
            // r2 stays in-progress; r3 stays in-progress.
        }

        using var s = _sp.CreateScope();
        var queue2 = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        var snapshot = await queue2.GetThroughputAsync(TimeSpan.FromHours(1));

        snapshot.ByType.Should().ContainKey(ReviewType.BlReview);
        snapshot.ByType[ReviewType.BlReview].Total.Should().Be(2);
        snapshot.ByType[ReviewType.BlReview].Completed.Should().Be(1);
        snapshot.ByType[ReviewType.BlReview].InProgress.Should().Be(1);

        snapshot.ByType.Should().ContainKey(ReviewType.AiTriage);
        snapshot.ByType[ReviewType.AiTriage].Total.Should().Be(1);
        snapshot.ByType[ReviewType.AiTriage].Completed.Should().Be(0);
    }

    [Fact]
    public async Task GetThroughput_window_excludes_older_rows()
    {
        var (svc, loc, user) = await SeedTenancyAsync();
        // Manually seed an older AnalystReview row.
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var session = new ReviewSession
            {
                Id = Guid.NewGuid(),
                CaseId = Guid.NewGuid(),
                AnalystUserId = user,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-30),
                Outcome = "completed",
                TenantId = _tenantId,
            };
            db.ReviewSessions.Add(session);
            db.AnalystReviews.Add(new AnalystReview
            {
                Id = Guid.NewGuid(),
                ReviewSessionId = session.Id,
                ReviewType = ReviewType.BlReview,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                CompletedAt = DateTimeOffset.UtcNow.AddDays(-30),
                Outcome = "completed",
                TenantId = _tenantId,
            });
            await db.SaveChangesAsync();
        }

        using var s = _sp.CreateScope();
        var queue = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        // Only look back 1 hour — the 30-day-old row should be excluded.
        var snapshot = await queue.GetThroughputAsync(TimeSpan.FromHours(1));
        snapshot.ByType.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMyQueue_user_with_no_membership_sees_empty()
    {
        var (svc, loc, _) = await SeedTenancyAsync();
        await SeedCaseAsync(loc);
        var stranger = Guid.NewGuid();

        using var s = _sp.CreateScope();
        var queue = s.ServiceProvider.GetRequiredService<ReviewQueueService>();
        var rows = await queue.GetMyQueueAsync(stranger);
        rows.Should().BeEmpty();
    }

    /// <summary>In-memory event publisher matching the rest of the test fixture.</summary>
    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<DomainEvent> Events { get; } = new();
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.FromResult(evt);
        }
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            Events.AddRange(events);
            return Task.FromResult<IReadOnlyList<DomainEvent>>(events);
        }
    }
}
