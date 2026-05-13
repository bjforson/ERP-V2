using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Workflows;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

public sealed class AuditReviewConsumerTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly RecordingEventPublisher _events = new();
    private readonly CapturingTransactionalQueue _submissionQueue = new();

    public AuditReviewConsumerTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("audit-review-consumer-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProcessAsync_CompletedConcurCreatesVerdictAndEnqueuesSubmission()
    {
        var c = await SeedCaseAsync();
        var review = await SeedAuditReviewAsync(c.Id, outcome: "concur", completed: true);
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new AuditReviewPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: "corr-ar"),
            CancellationToken.None);

        var updatedCase = await _db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        updatedCase.State.Should().Be(InspectionWorkflowState.Verdict);

        var verdict = await _db.Verdicts.AsNoTracking().SingleAsync(v => v.CaseId == c.Id);
        verdict.Decision.Should().Be(VerdictDecision.Clear);
        var submission = await _db.OutboundSubmissions.AsNoTracking().SingleAsync(s => s.CaseId == c.Id);
        submission.Status.Should().Be("pending");

        _submissionQueue.Db.Should().BeSameAs(_db);
        _submissionQueue.Request.Should().NotBeNull();
        _submissionQueue.Request!.WorkItemId.Should().Be(workItemId);
        _submissionQueue.Request.Payload.CaseId.Should().Be(c.Id);
        _submissionQueue.Request.Payload.OutboundSubmissionId.Should().Be(submission.Id);
        _submissionQueue.Request.Payload.ExternalSystemInstanceId.Should().Be(submission.ExternalSystemInstanceId);
        _submissionQueue.Request.CorrelationId.Should().Be("corr-ar");

        var evt = _events.Events.Single(e => e.EventType == "inspection.audit_review.routed");
        evt.Payload.GetProperty("reviewId").GetGuid().Should().Be(review.Id);
        evt.Payload.GetProperty("enqueuedSubmission").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ProcessAsync_SystemFallbackCompletesOpenAuditReview()
    {
        var c = await SeedCaseAsync();
        var review = await SeedAuditReviewAsync(
            c.Id,
            outcome: null,
            completed: false,
            userId: Guid.Empty);
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new AuditReviewPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: null),
            CancellationToken.None);

        var savedReview = await _db.AnalystReviews.AsNoTracking().SingleAsync(r => r.Id == review.Id);
        savedReview.Outcome.Should().Be("concur");
        savedReview.CompletedAt.Should().NotBeNull();

        var findings = await _db.Findings.AsNoTracking().ToListAsync();
        findings.Should().ContainSingle(f =>
            f.AnalystReviewId == review.Id
            && f.FindingType == "review.audit.system_auto_concur");
        (await _db.Verdicts.AsNoTracking().SingleAsync(v => v.CaseId == c.Id))
            .Decision.Should().Be(VerdictDecision.Clear);
        (await _db.OutboundSubmissions.AsNoTracking().CountAsync(s => s.CaseId == c.Id))
            .Should().Be(1);
        _submissionQueue.Request.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_HoldRoutesCaseToExceptionQueueWithoutSubmission()
    {
        var c = await SeedCaseAsync();
        await SeedAuditReviewAsync(c.Id, outcome: "hold", completed: true);
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new AuditReviewPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: null),
            CancellationToken.None);

        var updatedCase = await _db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        updatedCase.State.Should().Be(InspectionWorkflowState.Reviewed);
        updatedCase.ReviewQueue.Should().Be(ReviewQueue.Exception);
        (await _db.Verdicts.AsNoTracking().CountAsync(v => v.CaseId == c.Id)).Should().Be(0);
        _submissionQueue.Request.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_OpenHumanAuditReviewThrowsInsteadOfCompletingAsNoOp()
    {
        var c = await SeedCaseAsync();
        var workItemId = Guid.NewGuid();
        await SeedAuditReviewAsync(c.Id, outcome: null, completed: false, userId: Guid.NewGuid());

        var act = async () => await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new AuditReviewPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: null),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _submissionQueue.Request.Should().BeNull();
    }

    private AuditReviewConsumer NewConsumer()
        => new(
            _db,
            _tenant,
            _events,
            _submissionQueue,
            NullLogger<AuditReviewConsumer>.Instance);

    private async Task<InspectionCase> SeedCaseAsync()
    {
        var c = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = "MSCU1234567",
            State = InspectionWorkflowState.Assigned,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        _db.Cases.Add(c);
        _db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = Guid.NewGuid(),
            TypeCode = "test-authority",
            DisplayName = "Test Authority",
            Scope = ExternalSystemBindingScope.Shared,
            ConfigJson = "{}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        });
        await _db.SaveChangesAsync();
        return c;
    }

    private async Task<AnalystReview> SeedAuditReviewAsync(
        Guid caseId,
        string? outcome,
        bool completed,
        Guid? userId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var user = userId ?? Guid.NewGuid();
        var session = new ReviewSession
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            AnalystUserId = user,
            StartedAt = now.AddMinutes(-5),
            EndedAt = completed ? now : null,
            Outcome = completed ? "completed" : "in-progress",
            TenantId = 1
        };
        var review = new AnalystReview
        {
            Id = Guid.NewGuid(),
            ReviewSessionId = session.Id,
            ReviewType = ReviewType.AuditReview,
            Outcome = outcome,
            CompletedAt = completed ? now : null,
            CreatedAt = now.AddMinutes(-4),
            StartedByUserId = user,
            ConfidenceScore = 1.0,
            TenantId = 1
        };
        _db.ReviewSessions.Add(session);
        _db.AnalystReviews.Add(review);
        await _db.SaveChangesAsync();
        return review;
    }

    private sealed class CapturingTransactionalQueue : ITransactionalQueue<OutboundSubmissionPayload>
    {
        public DbContext? Db { get; private set; }
        public EnqueueRequest<OutboundSubmissionPayload>? Request { get; private set; }

        public Task<long> EnqueueAsync(
            DbContext db,
            EnqueueRequest<OutboundSubmissionPayload> request,
            CancellationToken ct = default)
        {
            Db = db;
            Request = request;
            return Task.FromResult(1L);
        }
    }

    private sealed class StubQueueClaim : IQueueClaim<AuditReviewPayload>
    {
        public StubQueueClaim(Guid workItemId, AuditReviewPayload payload, string? correlationId)
        {
            WorkItemId = workItemId;
            Payload = payload;
            CorrelationId = correlationId;
        }

        public long Id => 1;
        public Guid WorkItemId { get; }
        public long TenantId => 1;
        public int AttemptCount => 1;
        public AuditReviewPayload Payload { get; }
        public DateTimeOffset ClaimedUntil => DateTimeOffset.UtcNow.AddMinutes(5);
        public string? CorrelationId { get; }

        public Task CompleteAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task FailAsync(Exception error, TimeSpan? retryAfter = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RenewLeaseAsync(TimeSpan additional, CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
