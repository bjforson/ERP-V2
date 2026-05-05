using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Detection;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Imaging;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 31 / B5.2 Phase D — coverage for the
/// <see cref="CrossRecordScanService"/> + the
/// <see cref="CaseWorkflowService.SplitCaseAsync"/> hand-off.
/// </summary>
public sealed class CrossRecordScanServiceTests : IDisposable
{
    private readonly InspectionDbContext _db;
    private readonly TenantContext _tenant;
    private readonly RecordingEventPublisher _events;
    private readonly CaseWorkflowService _workflow;
    private readonly StubDetector _detector;

    public CrossRecordScanServiceTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("crs-service-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
        _tenant = new TenantContext();
        _tenant.SetTenant(1);
        _events = new RecordingEventPublisher();
        _detector = new StubDetector();

        _workflow = new CaseWorkflowService(
            db: _db,
            events: _events,
            plugins: new NoopPluginRegistry(),
            services: new EmptyServiceProvider(),
            tenant: _tenant,
            auth: new AnonymousAuthStateProvider(),
            imageStore: new NoopImageStore(),
            logger: NullLogger<CaseWorkflowService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private CrossRecordScanService NewService()
        => new(
            _db,
            new ICrossRecordScanDetector[] { _detector },
            _tenant,
            _events,
            _workflow,
            NullLogger<CrossRecordScanService>.Instance);

    private async Task<InspectionCase> SeedCaseAsync(string subject)
    {
        var c = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = subject,
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        _db.Cases.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task Detector_negative_does_not_persist_a_row()
    {
        var c = await SeedCaseAsync("X");
        _detector.NextResult = null;
        var service = NewService();
        var rows = await service.ScanAndPersistAsync(c.Id);
        rows.Should().BeEmpty();
        (await _db.CrossRecordDetections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Detector_positive_persists_pending_row()
    {
        var c = await SeedCaseAsync("X");
        _detector.NextResult = new CrossRecordDetectionDescriptor(
            c.Id,
            new[]
            {
                new CrossRecordSubject("X", "primary"),
                new CrossRecordSubject("Y", "evidence")
            },
            "two subjects detected");
        var service = NewService();
        var rows = await service.ScanAndPersistAsync(c.Id);
        rows.Should().HaveCount(1);
        rows[0].State.Should().Be(CrossRecordDetectionState.Pending);
        var rowDb = await _db.CrossRecordDetections.AsNoTracking().SingleAsync();
        rowDb.CaseId.Should().Be(c.Id);
        rowDb.DetectorVersion.Should().Be("test-detector");
    }

    [Fact]
    public async Task Idempotent_re_detection_updates_pending_row_in_place()
    {
        var c = await SeedCaseAsync("X");
        _detector.NextResult = new CrossRecordDetectionDescriptor(
            c.Id,
            new[] { new CrossRecordSubject("X", "p"), new CrossRecordSubject("Y", "e") },
            "v1");
        var service = NewService();
        await service.ScanAndPersistAsync(c.Id);
        await service.ScanAndPersistAsync(c.Id);
        // Still exactly one row — the unique (CaseId, DetectorVersion)
        // index keeps the table small.
        (await _db.CrossRecordDetections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Confirm_flips_to_Confirmed()
    {
        var c = await SeedCaseAsync("X");
        _detector.NextResult = new CrossRecordDetectionDescriptor(
            c.Id, new[] { new CrossRecordSubject("X", "p"), new CrossRecordSubject("Y", "e") }, "v1");
        var service = NewService();
        var row = (await service.ScanAndPersistAsync(c.Id)).Single();

        var confirmed = await service.ConfirmAsync(row.Id, actorUserId: Guid.NewGuid(), notes: "ok");
        confirmed.State.Should().Be(CrossRecordDetectionState.Confirmed);
        confirmed.ReviewedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Dismiss_flips_to_Dismissed()
    {
        var c = await SeedCaseAsync("X");
        _detector.NextResult = new CrossRecordDetectionDescriptor(
            c.Id, new[] { new CrossRecordSubject("X", "p"), new CrossRecordSubject("Y", "e") }, "v1");
        var service = NewService();
        var row = (await service.ScanAndPersistAsync(c.Id)).Single();

        var dismissed = await service.DismissAsync(row.Id, actorUserId: Guid.NewGuid(), notes: "false-positive");
        dismissed.State.Should().Be(CrossRecordDetectionState.Dismissed);
    }

    [Fact]
    public async Task ExecuteSplit_creates_child_cases_and_flips_to_Split()
    {
        var c = await SeedCaseAsync("X");
        _detector.NextResult = new CrossRecordDetectionDescriptor(
            c.Id, new[]
            {
                new CrossRecordSubject("X", "primary"),
                new CrossRecordSubject("Y", "second"),
                new CrossRecordSubject("Z", "third")
            }, "v1");
        var service = NewService();
        var row = (await service.ScanAndPersistAsync(c.Id)).Single();

        var split = await service.ExecuteSplitAsync(row.Id, actorUserId: Guid.NewGuid());
        split.State.Should().Be(CrossRecordDetectionState.Split);
        split.SplitCaseIdsJson.Should().NotBeNull();

        var childIds = JsonSerializer.Deserialize<List<Guid>>(split.SplitCaseIdsJson!)!;
        // Y + Z spawn child cases. X is the parent's own subject and is filtered.
        childIds.Should().HaveCount(2);

        var childRows = await _db.Cases.AsNoTracking()
            .Where(cc => childIds.Contains(cc.Id)).ToListAsync();
        childRows.Should().HaveCount(2);
        childRows.Select(cc => cc.SubjectIdentifier).Should().BeEquivalentTo(new[] { "Y", "Z" });
    }

    [Fact]
    public async Task ExecuteSplit_idempotent_on_already_Split()
    {
        var c = await SeedCaseAsync("X");
        _detector.NextResult = new CrossRecordDetectionDescriptor(
            c.Id, new[] { new CrossRecordSubject("X", "p"), new CrossRecordSubject("Y", "e") }, "v1");
        var service = NewService();
        var row = (await service.ScanAndPersistAsync(c.Id)).Single();
        await service.ExecuteSplitAsync(row.Id, actorUserId: Guid.NewGuid());
        // Re-execute — idempotent, no extra child cases spawned.
        var second = await service.ExecuteSplitAsync(row.Id, actorUserId: Guid.NewGuid());
        second.State.Should().Be(CrossRecordDetectionState.Split);
        // Still exactly two child cases (parent X + Y child).
        (await _db.Cases.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Cannot_confirm_a_Split_row()
    {
        var c = await SeedCaseAsync("X");
        _detector.NextResult = new CrossRecordDetectionDescriptor(
            c.Id, new[] { new CrossRecordSubject("X", "p"), new CrossRecordSubject("Y", "e") }, "v1");
        var service = NewService();
        var row = (await service.ScanAndPersistAsync(c.Id)).Single();
        await service.ExecuteSplitAsync(row.Id, actorUserId: null);

        var act = async () => await service.ConfirmAsync(row.Id, null, null);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListAsync_filters_by_state()
    {
        var c = await SeedCaseAsync("X");
        _detector.NextResult = new CrossRecordDetectionDescriptor(
            c.Id, new[] { new CrossRecordSubject("X", "p"), new CrossRecordSubject("Y", "e") }, "v1");
        var service = NewService();
        var row = (await service.ScanAndPersistAsync(c.Id)).Single();

        (await service.ListAsync(CrossRecordDetectionState.Pending)).Should().HaveCount(1);
        (await service.ListAsync(CrossRecordDetectionState.Confirmed)).Should().BeEmpty();
        await service.DismissAsync(row.Id, null, "no");
        (await service.ListAsync(CrossRecordDetectionState.Pending)).Should().BeEmpty();
        (await service.ListAsync(CrossRecordDetectionState.Dismissed)).Should().HaveCount(1);
    }

    private sealed class StubDetector : ICrossRecordScanDetector
    {
        public string DetectorVersion => "test-detector";
        public CrossRecordDetectionDescriptor? NextResult { get; set; }
        public Task<CrossRecordDetectionDescriptor?> DetectAsync(Guid caseId, CancellationToken ct = default)
            => Task.FromResult(NextResult);
    }

    private sealed class NoopPluginRegistry : IPluginRegistry
    {
        public IReadOnlyList<RegisteredPlugin> All => Array.Empty<RegisteredPlugin>();
        public IReadOnlyList<RegisteredPlugin> ForContract(Type contract) => Array.Empty<RegisteredPlugin>();
        public RegisteredPlugin? FindByTypeCode(string module, string typeCode) => null;
        public T Resolve<T>(string moduleId, string typeCode, IServiceProvider sp) where T : class
            => throw new NotSupportedException();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class AnonymousAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal()));
    }

    private sealed class NoopImageStore : IImageStore
    {
        public Task<string> SaveSourceAsync(string contentHash, string fileExtension, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
            => Task.FromResult($"noop://{contentHash}{fileExtension}");
        public Task<byte[]> ReadSourceAsync(string contentHash, string fileExtension, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<byte>());
        public Task<string> SaveRenderAsync(Guid scanArtifactId, string kind, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
            => Task.FromResult($"noop://{scanArtifactId}/{kind}");
        public Stream? OpenRenderRead(Guid scanArtifactId, string kind) => null;
    }
}
