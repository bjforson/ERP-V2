using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Workflows;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

public sealed class DecisionAgentConsumerTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly RecordingEventPublisher _events = new();
    private readonly CapturingTransactionalQueue _auditAssignmentQueue = new();

    public DecisionAgentConsumerTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("decision-agent-consumer-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProcessAsync_ReferRecommendationForErrorFindingsAndEnqueuesAuditAssignment()
    {
        var c = await SeedCaseAsync();
        await AddFindingAsync(c.Id, severity: "error");
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new DecisionAgentPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: "corr-da"),
            CancellationToken.None);

        var evt = _events.Events.Single(e => e.EventType == "inspection.decision_agent.scored");
        evt.CorrelationId.Should().Be("corr-da");
        evt.Payload.GetProperty("recommendation").GetString().Should().Be("refer");
        evt.Payload.GetProperty("findingCount").GetInt32().Should().Be(1);

        _auditAssignmentQueue.Db.Should().BeSameAs(_db);
        _auditAssignmentQueue.Request.Should().NotBeNull();
        _auditAssignmentQueue.Request!.WorkItemId.Should().Be(workItemId);
        _auditAssignmentQueue.Request.Payload.CaseId.Should().Be(c.Id);
        _auditAssignmentQueue.Request.Payload.WorkItemId.Should().Be(workItemId);
        _auditAssignmentQueue.Request.CorrelationId.Should().Be("corr-da");
        _auditAssignmentQueue.Request.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProcessAsync_InspectRecommendationForWarningFindings()
    {
        var c = await SeedCaseAsync();
        await AddFindingAsync(c.Id, severity: "warning");
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new DecisionAgentPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: null),
            CancellationToken.None);

        var evt = _events.Events.Single(e => e.EventType == "inspection.decision_agent.scored");
        evt.Payload.GetProperty("recommendation").GetString().Should().Be("inspect");
    }

    [Fact]
    public async Task ProcessAsync_ClearRecommendationWhenNoFindingsExist()
    {
        var c = await SeedCaseAsync();
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new DecisionAgentPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: null),
            CancellationToken.None);

        var evt = _events.Events.Single(e => e.EventType == "inspection.decision_agent.scored");
        evt.Payload.GetProperty("recommendation").GetString().Should().Be("clear");
        evt.Payload.GetProperty("findingCount").GetInt32().Should().Be(0);
        _auditAssignmentQueue.Request.Should().NotBeNull();
    }

    private DecisionAgentConsumer NewConsumer()
        => new(
            _db,
            _tenant,
            _events,
            _auditAssignmentQueue,
            NullLogger<DecisionAgentConsumer>.Instance);

    private async Task<InspectionCase> SeedCaseAsync()
    {
        var locId = Guid.NewGuid();
        _db.Locations.Add(new Location
        {
            Id = locId,
            Code = "tema",
            Name = "Tema Port",
            TenantId = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var c = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = locId,
            SubjectIdentifier = "MSCU1234567",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        _db.Cases.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    private async Task AddFindingAsync(Guid caseId, string severity)
    {
        var session = new ReviewSession
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            AnalystUserId = Guid.Empty,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = "engine-validation",
            TenantId = 1
        };
        var review = new AnalystReview
        {
            Id = Guid.NewGuid(),
            ReviewSessionId = session.Id,
            TimeToDecisionMs = 0,
            ConfidenceScore = 1.0,
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewType = ReviewType.EngineValidation,
            TenantId = 1
        };
        _db.ReviewSessions.Add(session);
        _db.AnalystReviews.Add(review);
        _db.Findings.Add(new Finding
        {
            Id = Guid.NewGuid(),
            AnalystReviewId = review.Id,
            FindingType = "validation.test",
            Severity = severity,
            LocationInImageJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        });
        await _db.SaveChangesAsync();
    }

    private sealed class CapturingTransactionalQueue : ITransactionalQueue<AuditAssignmentPayload>
    {
        public DbContext? Db { get; private set; }
        public EnqueueRequest<AuditAssignmentPayload>? Request { get; private set; }

        public Task<long> EnqueueAsync(
            DbContext db,
            EnqueueRequest<AuditAssignmentPayload> request,
            CancellationToken ct = default)
        {
            Db = db;
            Request = request;
            return Task.FromResult(1L);
        }
    }

    private sealed class StubQueueClaim : IQueueClaim<DecisionAgentPayload>
    {
        public StubQueueClaim(Guid workItemId, DecisionAgentPayload payload, string? correlationId)
        {
            WorkItemId = workItemId;
            Payload = payload;
            CorrelationId = correlationId;
        }

        public long Id => 1;
        public Guid WorkItemId { get; }
        public long TenantId => 1;
        public int AttemptCount => 1;
        public DecisionAgentPayload Payload { get; }
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
