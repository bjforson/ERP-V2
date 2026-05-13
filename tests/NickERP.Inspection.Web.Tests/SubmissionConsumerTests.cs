using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Workflows;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Platform.Plugins;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

public sealed class SubmissionConsumerTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly RecordingEventPublisher _events = new();
    private readonly RecordingExternalSystemAdapter _adapter = new();
    private readonly TestPluginRegistry _plugins;

    public SubmissionConsumerTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("submission-consumer-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
        _plugins = new TestPluginRegistry(_adapter);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProcessAsync_AcceptedSubmissionUpdatesRowAndCase()
    {
        var (c, submission) = await SeedPendingSubmissionAsync();
        _adapter.NextSubmissionResult = new SubmissionResult(
            Accepted: true,
            AuthorityResponseJson: "{\"accepted\":true}",
            Error: null);
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new OutboundSubmissionPayload(workItemId, c.Id, DateTimeOffset.UtcNow)
                {
                    OutboundSubmissionId = submission.Id,
                    ExternalSystemInstanceId = submission.ExternalSystemInstanceId,
                    IdempotencyKey = submission.IdempotencyKey
                },
                correlationId: "corr-sub"),
            CancellationToken.None);

        var saved = await _db.OutboundSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        saved.Status.Should().Be("accepted");
        saved.ResponseJson.Should().Be("{\"accepted\":true}");
        saved.RespondedAt.Should().NotBeNull();
        saved.LastAttemptAt.Should().NotBeNull();

        var savedCase = await _db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        savedCase.State.Should().Be(InspectionWorkflowState.Submitted);

        _adapter.SubmitCalls.Should().Be(1);
        _adapter.LastRequest.Should().NotBeNull();
        _adapter.LastRequest!.IdempotencyKey.Should().Be(submission.IdempotencyKey);
        _adapter.LastRequest.AuthorityReferenceNumber.Should().Be("BOE-001");

        var evt = _events.Events.Single(e => e.EventType == "nickerp.inspection.submission_dispatched");
        evt.CorrelationId.Should().Be("corr-sub");
        evt.Payload.GetProperty("Status").GetString().Should().Be("accepted");
    }

    [Fact]
    public async Task ProcessAsync_RejectedSubmissionDoesNotAdvanceCase()
    {
        var (c, submission) = await SeedPendingSubmissionAsync();
        _adapter.NextSubmissionResult = new SubmissionResult(false, "{\"accepted\":false}", "rejected by authority");
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new OutboundSubmissionPayload(workItemId, c.Id, DateTimeOffset.UtcNow)
                {
                    OutboundSubmissionId = submission.Id
                },
                correlationId: null),
            CancellationToken.None);

        var saved = await _db.OutboundSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        saved.Status.Should().Be("rejected");
        saved.ErrorMessage.Should().Be("rejected by authority");
        (await _db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id))
            .State.Should().Be(InspectionWorkflowState.Verdict);
    }

    [Fact]
    public async Task ProcessAsync_AdapterThrowMarksErrorAndRethrowsForQueueRetry()
    {
        var (c, submission) = await SeedPendingSubmissionAsync();
        _adapter.ShouldThrowOnSubmit = true;
        var workItemId = Guid.NewGuid();

        var act = async () => await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new OutboundSubmissionPayload(workItemId, c.Id, DateTimeOffset.UtcNow)
                {
                    OutboundSubmissionId = submission.Id
                },
                correlationId: null),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var saved = await _db.OutboundSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        saved.Status.Should().Be("error");
        saved.RetryCount.Should().Be(1);
        saved.ErrorMessage.Should().Contain("simulated authority crash");
    }

    [Fact]
    public async Task ProcessAsync_TerminalSubmissionIsIdempotentNoOp()
    {
        var (c, submission) = await SeedPendingSubmissionAsync(status: "accepted");
        var workItemId = Guid.NewGuid();

        await NewConsumer().ProcessAsync(
            new StubQueueClaim(
                workItemId,
                new OutboundSubmissionPayload(workItemId, c.Id, DateTimeOffset.UtcNow)
                {
                    OutboundSubmissionId = submission.Id
                },
                correlationId: null),
            CancellationToken.None);

        _adapter.SubmitCalls.Should().Be(0);
    }

    private SubmissionConsumer NewConsumer()
        => new(
            _db,
            _tenant,
            _plugins,
            services: EmptyServiceProvider.Instance,
            _events,
            NullLogger<SubmissionConsumer>.Instance);

    private async Task<(InspectionCase Case, OutboundSubmission Submission)> SeedPendingSubmissionAsync(
        string status = "pending")
    {
        var instance = new ExternalSystemInstance
        {
            Id = Guid.NewGuid(),
            TypeCode = "test-authority",
            DisplayName = "Test Authority",
            Scope = ExternalSystemBindingScope.Shared,
            ConfigJson = "{}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        var c = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = "MSCU1234567",
            State = InspectionWorkflowState.Verdict,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        var submission = new OutboundSubmission
        {
            Id = Guid.NewGuid(),
            CaseId = c.Id,
            ExternalSystemInstanceId = instance.Id,
            PayloadJson = "{\"decision\":\"Clear\"}",
            IdempotencyKey = "submission-key-" + Guid.NewGuid().ToString("N"),
            Status = status,
            SubmittedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        _db.ExternalSystemInstances.Add(instance);
        _db.Cases.Add(c);
        _db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = Guid.NewGuid(),
            CaseId = c.Id,
            ExternalSystemInstanceId = instance.Id,
            DocumentType = "BOE",
            ReferenceNumber = "BOE-001",
            PayloadJson = "{}",
            ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            TenantId = 1
        });
        _db.OutboundSubmissions.Add(submission);
        await _db.SaveChangesAsync();
        return (c, submission);
    }

    private sealed class RecordingExternalSystemAdapter : IExternalSystemAdapter
    {
        public string TypeCode => "test-authority";
        public ExternalSystemCapabilities Capabilities { get; } = new(
            SupportedDocumentTypes: new[] { "BOE" },
            SupportsPushNotifications: false,
            SupportsBulkFetch: true);

        public SubmissionResult NextSubmissionResult { get; set; } = new(true, "{}", null);
        public bool ShouldThrowOnSubmit { get; set; }
        public int SubmitCalls { get; private set; }
        public OutboundSubmissionRequest? LastRequest { get; private set; }

        public Task<ConnectionTestResult> TestAsync(ExternalSystemConfig config, CancellationToken ct = default)
            => Task.FromResult(new ConnectionTestResult(true, "ok"));

        public Task<IReadOnlyList<AuthorityDocumentDto>> FetchDocumentsAsync(
            ExternalSystemConfig config,
            CaseLookupCriteria lookup,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuthorityDocumentDto>>(Array.Empty<AuthorityDocumentDto>());

        public Task<SubmissionResult> SubmitAsync(
            ExternalSystemConfig config,
            OutboundSubmissionRequest request,
            CancellationToken ct = default)
        {
            SubmitCalls++;
            LastRequest = request;
            if (ShouldThrowOnSubmit)
            {
                throw new InvalidOperationException("simulated authority crash");
            }

            return Task.FromResult(NextSubmissionResult);
        }
    }

    private sealed class TestPluginRegistry : IPluginRegistry
    {
        private readonly IExternalSystemAdapter _adapter;

        public TestPluginRegistry(IExternalSystemAdapter adapter) => _adapter = adapter;

        public IReadOnlyList<RegisteredPlugin> All { get; } = Array.Empty<RegisteredPlugin>();

        public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType)
            => Array.Empty<RegisteredPlugin>();

        public RegisteredPlugin? FindByTypeCode(string module, string typeCode) => null;

        public T Resolve<T>(string module, string typeCode, IServiceProvider services)
            where T : class
        {
            if (typeof(T) == typeof(IExternalSystemAdapter)
                && string.Equals(module, "inspection", StringComparison.OrdinalIgnoreCase)
                && string.Equals(typeCode, _adapter.TypeCode, StringComparison.OrdinalIgnoreCase))
            {
                return (T)_adapter;
            }

            throw new KeyNotFoundException(typeCode);
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }

    private sealed class StubQueueClaim : IQueueClaim<OutboundSubmissionPayload>
    {
        public StubQueueClaim(Guid workItemId, OutboundSubmissionPayload payload, string? correlationId)
        {
            WorkItemId = workItemId;
            Payload = payload;
            CorrelationId = correlationId;
        }

        public long Id => 1;
        public Guid WorkItemId { get; }
        public long TenantId => 1;
        public int AttemptCount => 1;
        public OutboundSubmissionPayload Payload { get; }
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
