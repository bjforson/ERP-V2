using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Detection;
using NickERP.Inspection.Application.Workflows;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

public sealed class SplitDetectionConsumerTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly RecordingEventPublisher _events;
    private readonly StubDetector _detector = new();
    private readonly CapturingTransactionalQueue _imageAnalysisQueue = new();

    public SplitDetectionConsumerTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("split-detection-consumer-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
        _events = new RecordingEventPublisher();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProcessAsync_PersistsDetectedRowsAndEnqueuesImageAnalysis()
    {
        var c = await SeedCaseAsync("CONT001");
        var workItemId = Guid.NewGuid();
        _detector.NextResult = new CrossRecordDetectionDescriptor(
            c.Id,
            new[]
            {
                new CrossRecordSubject("CONT001", "primary"),
                new CrossRecordSubject("CONT999", "secondary")
            },
            "two subjects detected");

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new SplitDetectionPayload(c.Id, "case://cont001/primary"),
                correlationId: "corr-1"),
            CancellationToken.None);

        var row = await _db.CrossRecordDetections.AsNoTracking().SingleAsync();
        row.CaseId.Should().Be(c.Id);
        row.DetectorVersion.Should().Be("test-detector");
        row.State.Should().Be(CrossRecordDetectionState.Pending);

        _imageAnalysisQueue.Db.Should().BeSameAs(_db);
        _imageAnalysisQueue.Request.Should().NotBeNull();
        _imageAnalysisQueue.Request!.WorkItemId.Should().Be(workItemId);
        _imageAnalysisQueue.Request.Payload.CaseId.Should().Be(c.Id);
        _imageAnalysisQueue.Request.Payload.WorkItemId.Should().Be(workItemId);
        _imageAnalysisQueue.Request.CorrelationId.Should().Be("corr-1");
        _imageAnalysisQueue.Request.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
        _events.Events.Select(e => e.EventType)
            .Should().Contain("inspection.cross_record_detection.scanned");
    }

    [Fact]
    public async Task ProcessAsync_StillEnqueuesImageAnalysisWhenNoSplitFindingExists()
    {
        var c = await SeedCaseAsync("CONT001");
        var workItemId = Guid.NewGuid();
        _detector.NextResult = null;

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new SplitDetectionPayload(c.Id, "case://cont001/primary"),
                correlationId: null),
            CancellationToken.None);

        (await _db.CrossRecordDetections.CountAsync()).Should().Be(0);
        _imageAnalysisQueue.Request.Should().NotBeNull();
        _imageAnalysisQueue.Request!.WorkItemId.Should().Be(workItemId);
        _imageAnalysisQueue.Request.Payload.CaseId.Should().Be(c.Id);
    }

    private SplitDetectionConsumer NewConsumer()
    {
        var detection = new CrossRecordDetectionService(
            _db,
            new ICrossRecordScanDetector[] { _detector },
            _tenant,
            _events,
            NullLogger<CrossRecordDetectionService>.Instance);

        return new SplitDetectionConsumer(
            _db,
            detection,
            _imageAnalysisQueue,
            NullLogger<SplitDetectionConsumer>.Instance);
    }

    private async Task<InspectionCase> SeedCaseAsync(string subject)
    {
        var c = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = subject,
            State = InspectionWorkflowState.Validated,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        _db.Cases.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    private sealed class StubDetector : ICrossRecordScanDetector
    {
        public string DetectorVersion => "test-detector";
        public CrossRecordDetectionDescriptor? NextResult { get; set; }

        public Task<CrossRecordDetectionDescriptor?> DetectAsync(Guid caseId, CancellationToken ct = default)
            => Task.FromResult(NextResult);
    }

    private sealed class CapturingTransactionalQueue : ITransactionalQueue<ImageAnalysisPayload>
    {
        public DbContext? Db { get; private set; }
        public EnqueueRequest<ImageAnalysisPayload>? Request { get; private set; }

        public Task<long> EnqueueAsync(
            DbContext db,
            EnqueueRequest<ImageAnalysisPayload> request,
            CancellationToken ct = default)
        {
            Db = db;
            Request = request;
            return Task.FromResult(1L);
        }
    }

    private sealed class StubQueueClaim : IQueueClaim<SplitDetectionPayload>
    {
        public StubQueueClaim(Guid workItemId, SplitDetectionPayload payload, string? correlationId)
        {
            WorkItemId = workItemId;
            Payload = payload;
            CorrelationId = correlationId;
        }

        public long Id => 1;
        public Guid WorkItemId { get; }
        public long TenantId => 1;
        public int AttemptCount => 1;
        public SplitDetectionPayload Payload { get; }
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
