using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Reviews;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 34 / B6 Phase D — service-layer tests for
/// <see cref="ReviewWorkflow"/>. Covers the start/complete/escalate
/// happy paths, idempotent complete, audit-event payload shape, and
/// SLA window open + close integration with Sprint 31's
/// <see cref="SlaTracker"/>.
/// </summary>
public sealed class ReviewWorkflowTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly RecordingEventPublisher _events;
    private readonly InMemorySlaSettingsProvider _slaSettings;
    private readonly SlaTracker _sla;

    public ReviewWorkflowTests()
    {
        var dbOptions = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("review-workflow-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(dbOptions);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
        _events = new RecordingEventPublisher();
        _slaSettings = new InMemorySlaSettingsProvider();
        _sla = new SlaTracker(
            _db, _slaSettings, _tenant,
            Options.Create(new SlaTrackerOptions
            {
                FallbackBudgetMinutes = 60,
                AtRiskFraction = 0.5,
                DefaultBudgets = new(StringComparer.OrdinalIgnoreCase)
                {
                    [ReviewWorkflow.WindowNameFor(ReviewType.BlReview)] = 30,
                    [ReviewWorkflow.WindowNameFor(ReviewType.AiTriage)] = 20,
                    [ReviewWorkflow.WindowNameFor(ReviewType.AuditReview)] = 60,
                }
            }),
            NullLogger<SlaTracker>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private ReviewWorkflow NewWorkflow(bool withSla = true)
        => new(_db, _events, _tenant, NullLogger<ReviewWorkflow>.Instance, withSla ? _sla : null);

    private async Task<Guid> SeedCaseAsync()
    {
        var c = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = "CONT-S34-" + Guid.NewGuid().ToString("N")[..6],
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1,
        };
        _db.Cases.Add(c);
        await _db.SaveChangesAsync();
        return c.Id;
    }

    [Fact]
    public async Task StartReview_creates_AnalystReview_with_review_type()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow();

        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.BlReview, userId);

        var review = await _db.AnalystReviews.AsNoTracking().SingleAsync(r => r.Id == reviewId);
        review.ReviewType.Should().Be(ReviewType.BlReview);
        review.StartedByUserId.Should().Be(userId);
        review.CompletedAt.Should().BeNull();
        review.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task StartReview_emits_review_started_audit_event()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow();

        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.AiTriage, userId);

        var emitted = _events.Events.Should()
            .ContainSingle(e => e.EventType == "nickerp.inspection.review.started").Subject;
        emitted.EntityType.Should().Be("AnalystReview");
        emitted.EntityId.Should().Be(reviewId.ToString());
        emitted.ActorUserId.Should().Be(userId);
    }

    [Fact]
    public async Task StartReview_reuses_in_progress_session_when_present()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();

        // Pre-existing in-progress session — workflow should attach the
        // review to it instead of opening a new one.
        var existingSession = new ReviewSession
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            AnalystUserId = userId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Outcome = "in-progress",
            TenantId = 1,
        };
        _db.ReviewSessions.Add(existingSession);
        await _db.SaveChangesAsync();

        var reviewId = await NewWorkflow().StartReviewAsync(caseId, ReviewType.BlReview, userId);

        var review = await _db.AnalystReviews.AsNoTracking().SingleAsync();
        review.ReviewSessionId.Should().Be(existingSession.Id);
        // No second session should have been created.
        (await _db.ReviewSessions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task StartReview_opens_sla_window_named_per_type()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow(withSla: true);

        await workflow.StartReviewAsync(caseId, ReviewType.BlReview, userId);

        var window = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId);
        window.WindowName.Should().Be(ReviewWorkflow.WindowNameFor(ReviewType.BlReview));
        window.WindowName.Should().Be("review.blreview.elapsed");
        window.ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task StartReview_without_sla_tracker_does_not_throw()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow(withSla: false);

        var act = async () =>
            await workflow.StartReviewAsync(caseId, ReviewType.AuditReview, userId);

        await act.Should().NotThrowAsync();
        (await _db.SlaWindows.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CompleteReview_sets_outcome_and_completion_timestamp()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow();
        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.AiTriage, userId);

        var findings = new List<Finding>
        {
            new() { FindingType = "review.ai_triage.confirmed", Severity = "info" },
            new() { FindingType = "review.ai_triage.confirmed", Severity = "info" },
        };
        await workflow.CompleteReviewAsync(reviewId, "confirmed", findings, userId);

        var review = await _db.AnalystReviews.AsNoTracking().SingleAsync();
        review.Outcome.Should().Be("confirmed");
        review.CompletedAt.Should().NotBeNull();
        review.TimeToDecisionMs.Should().BeGreaterThanOrEqualTo(0);
        (await _db.Findings.CountAsync(f => f.AnalystReviewId == reviewId))
            .Should().Be(2);
    }

    [Fact]
    public async Task CompleteReview_emits_review_completed_audit_event()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow();
        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.BlReview, userId);
        _events.Events.Clear();

        await workflow.CompleteReviewAsync(reviewId, "completed", new List<Finding>(), userId);

        var emitted = _events.Events.Should()
            .ContainSingle(e => e.EventType == "nickerp.inspection.review.completed").Subject;
        emitted.EntityId.Should().Be(reviewId.ToString());
    }

    [Fact]
    public async Task CompleteReview_closes_sla_window()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow(withSla: true);
        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.BlReview, userId);

        await workflow.CompleteReviewAsync(reviewId, "completed", new List<Finding>(), userId);

        var window = await _db.SlaWindows.AsNoTracking()
            .SingleAsync(w => w.CaseId == caseId);
        window.ClosedAt.Should().NotBeNull();
        // Closed under budget → state flips to Closed (not Breached).
        window.State.Should().Be(SlaWindowState.Closed);
    }

    [Fact]
    public async Task CompleteReview_is_idempotent()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow();
        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.BlReview, userId);
        await workflow.CompleteReviewAsync(reviewId, "completed", new List<Finding>(), userId);

        var firstCompletedAt = (await _db.AnalystReviews.AsNoTracking()
            .SingleAsync()).CompletedAt;
        await Task.Delay(10);
        // Second call should be a no-op — review already completed.
        await workflow.CompleteReviewAsync(reviewId, "ignored", new List<Finding>(), userId);

        var review = await _db.AnalystReviews.AsNoTracking().SingleAsync();
        review.Outcome.Should().Be("completed");
        review.CompletedAt.Should().Be(firstCompletedAt);
    }

    [Fact]
    public async Task CompleteReview_overwrites_caller_supplied_finding_FK()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow();
        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.AuditReview, userId);

        // Caller supplies a typo'd FK; workflow must overwrite it.
        var typoFK = Guid.NewGuid();
        var findings = new List<Finding>
        {
            new() { AnalystReviewId = typoFK, FindingType = "review.audit.concur" },
        };
        await workflow.CompleteReviewAsync(reviewId, "concur", findings, userId);

        var saved = await _db.Findings.AsNoTracking().SingleAsync();
        saved.AnalystReviewId.Should().Be(reviewId);
        saved.AnalystReviewId.Should().NotBe(typoFK);
    }

    [Fact]
    public async Task CompleteReview_throws_on_empty_outcome()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow();
        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.BlReview, userId);

        var act = async () =>
            await workflow.CompleteReviewAsync(reviewId, "  ", new List<Finding>(), userId);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EscalateReview_re_targets_started_by_and_emits_audit()
    {
        var caseId = await SeedCaseAsync();
        var fromUser = Guid.NewGuid();
        var toUser = Guid.NewGuid();
        var workflow = NewWorkflow();
        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.AuditReview, fromUser);
        _events.Events.Clear();

        await workflow.EscalateReviewAsync(reviewId, fromUser, toUser, "needs supervisor");

        var review = await _db.AnalystReviews.AsNoTracking().SingleAsync();
        review.StartedByUserId.Should().Be(toUser);

        var emitted = _events.Events.Should()
            .ContainSingle(e => e.EventType == "nickerp.inspection.review.escalated").Subject;
        emitted.EntityId.Should().Be(reviewId.ToString());
    }

    [Fact]
    public async Task EscalateReview_throws_on_already_completed()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var toUser = Guid.NewGuid();
        var workflow = NewWorkflow();
        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.BlReview, userId);
        await workflow.CompleteReviewAsync(reviewId, "completed", new List<Finding>(), userId);

        var act = async () =>
            await workflow.EscalateReviewAsync(reviewId, userId, toUser, "too late");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task EscalateReview_throws_on_empty_reason()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var toUser = Guid.NewGuid();
        var workflow = NewWorkflow();
        var reviewId = await workflow.StartReviewAsync(caseId, ReviewType.AuditReview, userId);

        var act = async () =>
            await workflow.EscalateReviewAsync(reviewId, userId, toUser, "  ");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StartReview_throws_when_tenant_not_resolved()
    {
        var caseId = await SeedCaseAsync();
        // Build a fresh tenant context that's NOT resolved.
        var unresolved = new TenantContext();
        var workflow = new ReviewWorkflow(_db, _events, unresolved, NullLogger<ReviewWorkflow>.Instance, _sla);

        var act = async () =>
            await workflow.StartReviewAsync(caseId, ReviewType.BlReview, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartReview_throws_when_case_not_found()
    {
        var workflow = NewWorkflow();
        var act = async () =>
            await workflow.StartReviewAsync(Guid.NewGuid(), ReviewType.BlReview, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void WindowNameFor_produces_dotted_lowercase_format()
    {
        ReviewWorkflow.WindowNameFor(ReviewType.BlReview).Should().Be("review.blreview.elapsed");
        ReviewWorkflow.WindowNameFor(ReviewType.AiTriage).Should().Be("review.aitriage.elapsed");
        ReviewWorkflow.WindowNameFor(ReviewType.AuditReview).Should().Be("review.auditreview.elapsed");
        ReviewWorkflow.WindowNameFor(ReviewType.Standard).Should().Be("review.standard.elapsed");
    }

    [Fact]
    public async Task StartReview_two_types_on_same_case_open_two_windows()
    {
        var caseId = await SeedCaseAsync();
        var userId = Guid.NewGuid();
        var workflow = NewWorkflow();

        await workflow.StartReviewAsync(caseId, ReviewType.BlReview, userId);
        await workflow.StartReviewAsync(caseId, ReviewType.AiTriage, userId);

        var windows = await _db.SlaWindows.AsNoTracking()
            .Where(w => w.CaseId == caseId).ToListAsync();
        windows.Should().HaveCount(2);
        windows.Select(w => w.WindowName).Should().BeEquivalentTo(new[]
        {
            "review.blreview.elapsed",
            "review.aitriage.elapsed",
        });
    }

    /// <summary>
    /// In-memory IEventPublisher capturing every emitted DomainEvent for
    /// assertion. Mirrors the shape used in CompletenessCheckerTests.
    /// </summary>
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
