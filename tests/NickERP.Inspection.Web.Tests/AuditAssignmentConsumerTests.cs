using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Workflows;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

public sealed class AuditAssignmentConsumerTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly RecordingEventPublisher _events = new();
    private readonly CapturingTransactionalQueue _auditReviewQueue = new();

    public AuditAssignmentConsumerTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("audit-assignment-consumer-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProcessAsync_AssignsEligibleServiceUserAndWaitsForHumanAuditCompletion()
    {
        var c = await SeedCaseWithServiceAsync(hasUser: true);
        var serviceUser = await _db.AnalysisServiceUsers.AsNoTracking().SingleAsync();
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new AuditAssignmentPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: "corr-aa"),
            CancellationToken.None);

        var assignedCase = await _db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        assignedCase.State.Should().Be(InspectionWorkflowState.Assigned);
        assignedCase.AssignedAnalystUserId.Should().Be(serviceUser.UserId);

        var claim = await _db.CaseClaims.AsNoTracking().SingleAsync();
        claim.ClaimedByUserId.Should().Be(serviceUser.UserId);
        claim.AnalysisServiceId.Should().Be(serviceUser.AnalysisServiceId);

        var session = await _db.ReviewSessions.AsNoTracking().SingleAsync();
        session.AnalystUserId.Should().Be(serviceUser.UserId);
        var review = await _db.AnalystReviews.AsNoTracking().SingleAsync();
        review.ReviewType.Should().Be(ReviewType.AuditReview);
        review.ReviewSessionId.Should().Be(session.Id);

        _auditReviewQueue.Request.Should().BeNull();

        var evt = _events.Events.Single(e => e.EventType == "inspection.audit_assignment.assigned");
        evt.Payload.GetProperty("systemFallback").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ProcessAsync_FallsBackToSystemUserWhenNoServiceUserExists()
    {
        var c = await SeedCaseWithServiceAsync(hasUser: false);
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new AuditAssignmentPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: null),
            CancellationToken.None);

        var assignedCase = await _db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        assignedCase.AssignedAnalystUserId.Should().Be(Guid.Empty);

        var claim = await _db.CaseClaims.AsNoTracking().SingleAsync();
        claim.ClaimedByUserId.Should().Be(Guid.Empty);

        _auditReviewQueue.Request.Should().NotBeNull();
        _auditReviewQueue.Request!.WorkItemId.Should().Be(workItemId);
        _auditReviewQueue.Request.Payload.CaseId.Should().Be(c.Id);

        var evt = _events.Events.Single(e => e.EventType == "inspection.audit_assignment.assigned");
        evt.Payload.GetProperty("systemFallback").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ProcessAsync_ReusesExistingActiveClaim()
    {
        var c = await SeedCaseWithServiceAsync(hasUser: true);
        var service = await _db.AnalysisServices.AsNoTracking().SingleAsync();
        var existingUserId = Guid.NewGuid();
        _db.CaseClaims.Add(new CaseClaim
        {
            Id = Guid.NewGuid(),
            CaseId = c.Id,
            AnalysisServiceId = service.Id,
            ClaimedByUserId = existingUserId,
            ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            TenantId = 1
        });
        await _db.SaveChangesAsync();
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new AuditAssignmentPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: null),
            CancellationToken.None);

        (await _db.CaseClaims.CountAsync()).Should().Be(1);
        var assignedCase = await _db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        assignedCase.AssignedAnalystUserId.Should().Be(existingUserId);
        var session = await _db.ReviewSessions.AsNoTracking().SingleAsync();
        session.AnalystUserId.Should().Be(existingUserId);
    }

    private AuditAssignmentConsumer NewConsumer()
        => new(
            _db,
            _tenant,
            _events,
            _auditReviewQueue,
            NullLogger<AuditAssignmentConsumer>.Instance);

    private async Task<InspectionCase> SeedCaseWithServiceAsync(bool hasUser)
    {
        var location = new Location
        {
            Id = Guid.NewGuid(),
            Code = "tema",
            Name = "Tema Port",
            TenantId = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var service = new AnalysisService
        {
            Id = Guid.NewGuid(),
            Name = "All Locations",
            IsBuiltInAllLocations = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        var c = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = location.Id,
            SubjectIdentifier = "MSCU1234567",
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };

        _db.Locations.Add(location);
        _db.AnalysisServices.Add(service);
        _db.AnalysisServiceLocations.Add(new AnalysisServiceLocation
        {
            AnalysisServiceId = service.Id,
            LocationId = location.Id,
            AddedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        });
        if (hasUser)
        {
            _db.AnalysisServiceUsers.Add(new AnalysisServiceUser
            {
                AnalysisServiceId = service.Id,
                UserId = Guid.NewGuid(),
                AssignedAt = DateTimeOffset.UtcNow.AddHours(-1),
                TenantId = 1
            });
        }
        _db.Cases.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    private sealed class CapturingTransactionalQueue : ITransactionalQueue<AuditReviewPayload>
    {
        public DbContext? Db { get; private set; }
        public EnqueueRequest<AuditReviewPayload>? Request { get; private set; }

        public Task<long> EnqueueAsync(
            DbContext db,
            EnqueueRequest<AuditReviewPayload> request,
            CancellationToken ct = default)
        {
            Db = db;
            Request = request;
            return Task.FromResult(1L);
        }
    }

    private sealed class StubQueueClaim : IQueueClaim<AuditAssignmentPayload>
    {
        public StubQueueClaim(Guid workItemId, AuditAssignmentPayload payload, string? correlationId)
        {
            WorkItemId = workItemId;
            Payload = payload;
            CorrelationId = correlationId;
        }

        public long Id => 1;
        public Guid WorkItemId { get; }
        public long TenantId => 1;
        public int AttemptCount => 1;
        public AuditAssignmentPayload Payload { get; }
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
