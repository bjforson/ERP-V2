using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Completeness;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Application.Workflows;
using NickERP.Inspection.Core.Completeness;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;
using NickERP.Inspection.Database;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

public sealed class ImageAnalysisConsumerTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly RecordingEventPublisher _events;
    private readonly InMemoryRuleEnablementProvider _ruleEnablement = new();
    private readonly CapturingTransactionalQueue _decisionQueue = new();

    public ImageAnalysisConsumerTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("image-analysis-consumer-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
        _events = new RecordingEventPublisher();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProcessAsync_RunsValidationCompletenessAndEnqueuesDecisionAgent()
    {
        var c = await SeedCaseAsync();
        var workItemId = Guid.NewGuid();
        var completeness = new RecordingCompletenessChecker();

        await NewConsumer(completeness).ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new ImageAnalysisPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: "corr-ia"),
            CancellationToken.None);

        (await _db.ValidationRuleSnapshots.CountAsync(s => s.CaseId == c.Id)).Should().Be(1);
        var finding = await _db.Findings.AsNoTracking().SingleAsync();
        finding.FindingType.Should().Be("validation.test.image_analysis");
        finding.Severity.Should().Be("warning");

        completeness.Calls.Should().Be(1);
        _decisionQueue.Db.Should().BeSameAs(_db);
        _decisionQueue.Request.Should().NotBeNull();
        _decisionQueue.Request!.WorkItemId.Should().Be(workItemId);
        _decisionQueue.Request.Payload.CaseId.Should().Be(c.Id);
        _decisionQueue.Request.Payload.WorkItemId.Should().Be(workItemId);
        _decisionQueue.Request.CorrelationId.Should().Be("corr-ia");
        _decisionQueue.Request.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProcessAsync_SkipsValidationWhenSnapshotsAlreadyExist()
    {
        var c = await SeedCaseAsync();
        var workItemId = Guid.NewGuid();
        _db.ValidationRuleSnapshots.Add(new ValidationRuleSnapshot
        {
            Id = Guid.NewGuid(),
            CaseId = c.Id,
            RuleId = "test.existing",
            Severity = (int)ValidationSeverity.Warning,
            Outcome = "warning",
            Message = "already evaluated",
            PropertiesJson = "{}",
            EvaluatedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        });
        await _db.SaveChangesAsync();

        await NewConsumer(new RecordingCompletenessChecker()).ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new ImageAnalysisPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: null),
            CancellationToken.None);

        (await _db.ValidationRuleSnapshots.CountAsync(s => s.CaseId == c.Id)).Should().Be(1);
        (await _db.Findings.CountAsync()).Should().Be(0);
        _decisionQueue.Request.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_SkipsCompletenessWhenEngineSessionAlreadyExists()
    {
        var c = await SeedCaseAsync();
        var workItemId = Guid.NewGuid();
        _db.ReviewSessions.Add(new ReviewSession
        {
            Id = Guid.NewGuid(),
            CaseId = c.Id,
            AnalystUserId = Guid.Empty,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = "completeness-engine",
            TenantId = 1
        });
        await _db.SaveChangesAsync();

        var completeness = new RecordingCompletenessChecker();
        await NewConsumer(completeness).ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new ImageAnalysisPayload(workItemId, c.Id, DateTimeOffset.UtcNow),
                correlationId: null),
            CancellationToken.None);

        completeness.Calls.Should().Be(0);
        _decisionQueue.Request.Should().NotBeNull();
    }

    private ImageAnalysisConsumer NewConsumer(RecordingCompletenessChecker completeness)
    {
        var validation = new ValidationEngine(
            _db,
            new IValidationRule[] { new ImageWarningRule() },
            _ruleEnablement,
            _events,
            _tenant,
            NullLogger<ValidationEngine>.Instance);

        return new ImageAnalysisConsumer(
            _db,
            validation,
            completeness,
            _decisionQueue,
            NullLogger<ImageAnalysisConsumer>.Instance);
    }

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

    private sealed class ImageWarningRule : IValidationRule
    {
        public string RuleId => "test.image_analysis";
        public string Description => "image-analysis warning";

        public ValidationOutcome Evaluate(ValidationContext context)
            => ValidationOutcome.Warn(RuleId, "image analysis warning");
    }

    private sealed class RecordingCompletenessChecker : ICompletenessChecker
    {
        public int Calls { get; private set; }

        public Task<CompletenessEvaluationResult> EvaluateAsync(Guid caseId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new CompletenessEvaluationResult(
                caseId,
                new[] { CompletenessOutcome.Pass("test.completeness") }));
        }
    }

    private sealed class CapturingTransactionalQueue : ITransactionalQueue<DecisionAgentPayload>
    {
        public DbContext? Db { get; private set; }
        public EnqueueRequest<DecisionAgentPayload>? Request { get; private set; }

        public Task<long> EnqueueAsync(
            DbContext db,
            EnqueueRequest<DecisionAgentPayload> request,
            CancellationToken ct = default)
        {
            Db = db;
            Request = request;
            return Task.FromResult(1L);
        }
    }

    private sealed class StubQueueClaim : IQueueClaim<ImageAnalysisPayload>
    {
        public StubQueueClaim(Guid workItemId, ImageAnalysisPayload payload, string? correlationId)
        {
            WorkItemId = workItemId;
            Payload = payload;
            CorrelationId = correlationId;
        }

        public long Id => 1;
        public Guid WorkItemId { get; }
        public long TenantId => 1;
        public int AttemptCount => 1;
        public ImageAnalysisPayload Payload { get; }
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
