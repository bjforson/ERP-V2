using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 24 / B3 — integration tests for the seven new B3 workers,
/// using the EF in-memory provider so the workers' DiscoveryAsync +
/// per-tenant + persistence logic exercises end-to-end without a
/// Postgres dependency.
///
/// <para>
/// One outer fixture seeds a tenant + the entities each worker needs;
/// individual tests assert per-worker behaviour after invoking the
/// internal one-shot drain method.
/// </para>
/// </summary>
public sealed class Sprint24WorkersTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly long _tenantId = 1;
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _scannerInstanceId = Guid.NewGuid();
    private readonly Guid _externalSystemInstanceId = Guid.NewGuid();
    private readonly RecordingScannerAdapter _scannerAdapter = new();
    private readonly RecordingCursorAdapter _cursorAdapter = new();
    private readonly RecordingExternalSystemAdapter _esAdapter = new();

    public Sprint24WorkersTests()
    {
        var dbName = "s24-b3-" + Guid.NewGuid();
        var services = new ServiceCollection();

        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContext<TenancyDbContext>(o =>
            o.UseInMemoryDatabase("tenancy-" + dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddSingleton(_scannerAdapter);
        services.AddSingleton(_cursorAdapter);
        services.AddSingleton(_esAdapter);
        services.AddSingleton<IPluginRegistry>(sp => new MultiPluginRegistry(_scannerAdapter, _cursorAdapter, _esAdapter));

        // Worker options — every worker is enabled so the SweepOnce /
        // PullOnce / etc methods don't no-op.
        services.Configure<ScannerHealthSweepOptions>(o => { o.Enabled = true; o.PollInterval = TimeSpan.FromSeconds(1); o.StartupDelay = TimeSpan.Zero; });
        services.Configure<AseSyncOptions>(o => { o.Enabled = true; o.PollInterval = TimeSpan.FromSeconds(1); o.StartupDelay = TimeSpan.Zero; o.BatchLimit = 100; o.MaxRecordsPerCycle = 500; });
        services.Configure<IcumsApiPullOptions>(o => { o.Enabled = true; o.PollInterval = TimeSpan.FromSeconds(1); o.StartupDelay = TimeSpan.Zero; });
        services.Configure<IcumsFileScannerOptions>(o => { o.Enabled = true; o.PollInterval = TimeSpan.FromSeconds(1); o.StartupDelay = TimeSpan.Zero; });
        services.Configure<IcumsSubmissionDispatchOptions>(o => { o.Enabled = true; o.PollInterval = TimeSpan.FromSeconds(1); o.StartupDelay = TimeSpan.Zero; o.BatchLimit = 50; });
        services.Configure<IcumsSubmissionResultPollerOptions>(o => { o.Enabled = true; o.PollInterval = TimeSpan.FromSeconds(1); o.StartupDelay = TimeSpan.Zero; o.BatchLimit = 50; });
        services.Configure<ContainerDataMatcherOptions>(o => { o.Enabled = true; o.PollInterval = TimeSpan.FromSeconds(1); o.StartupDelay = TimeSpan.Zero; o.CaptureWindow = TimeSpan.FromHours(24); o.BatchLimit = 200; });

        services.AddSingleton<ScannerHealthSweepWorker>(sp => new ScannerHealthSweepWorker(
            sp, sp.GetRequiredService<IOptions<ScannerHealthSweepOptions>>(),
            NullLogger<ScannerHealthSweepWorker>.Instance));
        services.AddSingleton<AseSyncWorker>(sp => new AseSyncWorker(
            sp, sp.GetRequiredService<IOptions<AseSyncOptions>>(),
            NullLogger<AseSyncWorker>.Instance));
        services.AddSingleton<AuthorityDocumentBackfillWorker>(sp => new AuthorityDocumentBackfillWorker(
            sp, sp.GetRequiredService<IOptions<IcumsApiPullOptions>>(),
            NullLogger<AuthorityDocumentBackfillWorker>.Instance));
        services.AddSingleton<AuthorityDocumentInboxWorker>(sp => new AuthorityDocumentInboxWorker(
            sp, sp.GetRequiredService<IOptions<IcumsFileScannerOptions>>(),
            NullLogger<AuthorityDocumentInboxWorker>.Instance));
        services.AddSingleton<OutboundSubmissionDispatchWorker>(sp => new OutboundSubmissionDispatchWorker(
            sp, sp.GetRequiredService<IOptions<IcumsSubmissionDispatchOptions>>(),
            NullLogger<OutboundSubmissionDispatchWorker>.Instance));
        services.AddSingleton<OutboundSubmissionResultPollerWorker>(sp => new OutboundSubmissionResultPollerWorker(
            sp, sp.GetRequiredService<IOptions<IcumsSubmissionResultPollerOptions>>(),
            NullLogger<OutboundSubmissionResultPollerWorker>.Instance));
        services.AddSingleton<AuthorityDocumentMatcherWorker>(sp => new AuthorityDocumentMatcherWorker(
            sp, sp.GetRequiredService<IOptions<ContainerDataMatcherOptions>>(),
            NullLogger<AuthorityDocumentMatcherWorker>.Instance));

        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    // -----------------------------------------------------------------
    // ScannerHealthSweepWorker
    // -----------------------------------------------------------------

    [Fact]
    public async Task ScannerHealthSweep_RunsTestAsyncOnEveryActiveDevice()
    {
        await SeedTenantAsync();
        await SeedScannerDeviceAsync();
        _scannerAdapter.NextTestResult = new NickERP.Inspection.Scanners.Abstractions.ConnectionTestResult(true, "ok");

        var worker = _sp.GetRequiredService<ScannerHealthSweepWorker>();
        var swept = await InvokeAsync<int>(worker, "SweepOnceAsync");

        Assert.Equal(1, swept);
        Assert.Equal(1, _scannerAdapter.TestInvocationCount);
    }

    [Fact]
    public async Task ScannerHealthSweep_RecordsResultEvenWhenAdapterReportsFailure()
    {
        await SeedTenantAsync();
        await SeedScannerDeviceAsync();
        _scannerAdapter.NextTestResult = new NickERP.Inspection.Scanners.Abstractions.ConnectionTestResult(false, "scanner offline");

        var worker = _sp.GetRequiredService<ScannerHealthSweepWorker>();
        var swept = await InvokeAsync<int>(worker, "SweepOnceAsync");

        Assert.Equal(1, swept);
        Assert.Equal(1, _scannerAdapter.TestInvocationCount);
    }

    [Fact]
    public async Task ScannerHealthSweep_NoActiveDevices_NoTestCalls()
    {
        await SeedTenantAsync();
        // No scanner device seeded.

        var worker = _sp.GetRequiredService<ScannerHealthSweepWorker>();
        var swept = await InvokeAsync<int>(worker, "SweepOnceAsync");

        Assert.Equal(0, swept);
        Assert.Equal(0, _scannerAdapter.TestInvocationCount);
    }

    [Fact]
    public async Task ScannerHealthSweep_AdapterThrows_ContinuesWithOtherDevices()
    {
        await SeedTenantAsync();
        await SeedScannerDeviceAsync();
        _scannerAdapter.ShouldThrowOnTest = true;

        var worker = _sp.GetRequiredService<ScannerHealthSweepWorker>();
        // Sweep should swallow the per-device exception and still
        // return 1 (one device visited).
        var swept = await InvokeAsync<int>(worker, "SweepOnceAsync");
        Assert.Equal(1, swept);
    }

    // -----------------------------------------------------------------
    // AseSyncWorker
    // -----------------------------------------------------------------

    [Fact]
    public async Task AseSync_PullsRecordsFromCursorAdapter()
    {
        await SeedTenantAsync();
        await SeedCursorScannerDeviceAsync();
        _cursorAdapter.NextBatch = new CursorSyncBatch(
            Records: new[]
            {
                new CursorSyncRecord(
                    DeviceId: _scannerInstanceId,
                    SourceReference: "row-1",
                    CapturedAt: DateTimeOffset.UtcNow,
                    Format: "image/png",
                    Bytes: new byte[] { 1, 2, 3 },
                    IdempotencyKey: "idem-1")
            },
            NextCursor: "cursor-after-1",
            HasMore: false);

        var worker = _sp.GetRequiredService<AseSyncWorker>();
        var pulled = await InvokeAsync<int>(worker, "PullOnceAsync");

        Assert.Equal(1, pulled);
        Assert.Equal(1, _cursorAdapter.PullInvocationCount);
        Assert.Equal(string.Empty, _cursorAdapter.LastCursorSeen); // first cycle starts at empty
    }

    [Fact]
    public async Task AseSync_AdvancesCursorBetweenCycles()
    {
        await SeedTenantAsync();
        await SeedCursorScannerDeviceAsync();
        _cursorAdapter.NextBatch = new CursorSyncBatch(
            Records: new[] { new CursorSyncRecord(_scannerInstanceId, "row-1", DateTimeOffset.UtcNow, "image/png", new byte[] { 1 }, "idem-1") },
            NextCursor: "cursor-2",
            HasMore: false);

        var worker = _sp.GetRequiredService<AseSyncWorker>();
        await InvokeAsync<int>(worker, "PullOnceAsync");

        // Second cycle — adapter should see the cursor we last returned.
        _cursorAdapter.NextBatch = new CursorSyncBatch(
            Records: System.Array.Empty<CursorSyncRecord>(),
            NextCursor: "cursor-2",
            HasMore: false);

        await InvokeAsync<int>(worker, "PullOnceAsync");

        Assert.Equal("cursor-2", _cursorAdapter.LastCursorSeen);
    }

    [Fact]
    public async Task AseSync_AdapterIsNotCursorSyncCapable_NoPull()
    {
        await SeedTenantAsync();
        await SeedScannerDeviceAsync(); // FS6000-style adapter, NOT cursor-capable

        var worker = _sp.GetRequiredService<AseSyncWorker>();
        var pulled = await InvokeAsync<int>(worker, "PullOnceAsync");

        Assert.Equal(0, pulled);
        Assert.Equal(0, _cursorAdapter.PullInvocationCount);
    }

    // -----------------------------------------------------------------
    // AuthorityDocumentBackfillWorker
    // -----------------------------------------------------------------

    [Fact]
    public async Task AuthorityDocBackfill_FetchesAndPersistsMatchingCases()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        await SeedCaseAsync(_locationId, "MSCU0000001");
        _esAdapter.NextDocs = new[]
        {
            new AuthorityDocumentDto(
                InstanceId: _externalSystemInstanceId,
                DocumentType: "BOE",
                ReferenceNumber: "BOE-001",
                ReceivedAt: DateTimeOffset.UtcNow,
                PayloadJson: "{\"container_number\":\"MSCU0000001\"}")
        };

        var worker = _sp.GetRequiredService<AuthorityDocumentBackfillWorker>();
        var fetched = await InvokeAsync<int>(worker, "BackfillOnceAsync");

        Assert.Equal(1, fetched);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var docs = await db.AuthorityDocuments.AsNoTracking().ToListAsync();
        var doc = Assert.Single(docs);
        Assert.Equal("BOE", doc.DocumentType);
        Assert.Equal("BOE-001", doc.ReferenceNumber);
    }

    [Fact]
    public async Task AuthorityDocBackfill_NoMatchingCase_DropsDocument()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        // No case seeded.
        _esAdapter.NextDocs = new[]
        {
            new AuthorityDocumentDto(
                InstanceId: _externalSystemInstanceId,
                DocumentType: "BOE",
                ReferenceNumber: "BOE-002",
                ReceivedAt: DateTimeOffset.UtcNow,
                PayloadJson: "{\"container_number\":\"NONEXISTENT\"}")
        };

        var worker = _sp.GetRequiredService<AuthorityDocumentBackfillWorker>();
        var fetched = await InvokeAsync<int>(worker, "BackfillOnceAsync");

        Assert.Equal(0, fetched); // dropped, not phantom-cased
    }

    [Fact]
    public async Task AuthorityDocBackfill_DuplicateRefSilentlyDropped()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        await SeedCaseAsync(_locationId, "MSCU0000002");

        var dto = new AuthorityDocumentDto(
            InstanceId: _externalSystemInstanceId,
            DocumentType: "BOE",
            ReferenceNumber: "BOE-003",
            ReceivedAt: DateTimeOffset.UtcNow,
            PayloadJson: "{\"container_number\":\"MSCU0000002\"}");

        _esAdapter.NextDocs = new[] { dto };
        var worker = _sp.GetRequiredService<AuthorityDocumentBackfillWorker>();
        await InvokeAsync<int>(worker, "BackfillOnceAsync");

        // Second cycle, same dto — should not double-insert.
        _esAdapter.NextDocs = new[] { dto };
        await InvokeAsync<int>(worker, "BackfillOnceAsync");

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var docs = await db.AuthorityDocuments.AsNoTracking().ToListAsync();
        Assert.Single(docs);
    }

    [Fact]
    public async Task AuthorityDocBackfill_BulkFetchUnsupported_NoOp()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        await SeedCaseAsync(_locationId, "MSCU0000003");
        _esAdapter.SupportsBulkFetch = false;

        _esAdapter.NextDocs = new[]
        {
            new AuthorityDocumentDto(_externalSystemInstanceId, "BOE", "BOE-004",
                DateTimeOffset.UtcNow, "{\"container_number\":\"MSCU0000003\"}")
        };

        var worker = _sp.GetRequiredService<AuthorityDocumentBackfillWorker>();
        var fetched = await InvokeAsync<int>(worker, "BackfillOnceAsync");

        Assert.Equal(0, fetched);
    }

    // -----------------------------------------------------------------
    // AuthorityDocumentInboxWorker
    // -----------------------------------------------------------------

    [Fact]
    public async Task AuthorityDocInbox_NoDropFolder_NoOp()
    {
        await SeedTenantAsync();
        // DropFolder option not set
        var worker = _sp.GetRequiredService<AuthorityDocumentInboxWorker>();
        var ingested = await InvokeAsync<int>(worker, "ScanOnceAsync");
        Assert.Equal(0, ingested);
    }

    [Fact]
    public async Task AuthorityDocInbox_IngestJsonFile()
    {
        var dropFolder = Path.Combine(Path.GetTempPath(), "s24-inbox-" + Guid.NewGuid());
        var subFolder = Path.Combine(dropFolder, "ContainerData");
        Directory.CreateDirectory(subFolder);
        var fileName = "container-001.json";
        var filePath = Path.Combine(subFolder, fileName);
        await File.WriteAllTextAsync(filePath,
            "{\"container_number\":\"MSCU0000004\",\"declaration\":\"DECL-9001\"}");

        try
        {
            await SeedTenantAsync();
            await SeedExternalSystemAsync();
            await SeedCaseAsync(_locationId, "MSCU0000004");

            // Reconfigure options for this test.
            var optsMonitor = _sp.GetRequiredService<IOptions<IcumsFileScannerOptions>>();
            optsMonitor.Value.DropFolder = dropFolder;
            optsMonitor.Value.ExpectedSubfolders = new[] { "ContainerData" };

            var worker = _sp.GetRequiredService<AuthorityDocumentInboxWorker>();
            var ingested = await InvokeAsync<int>(worker, "ScanOnceAsync");

            Assert.Equal(1, ingested);

            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var doc = await db.AuthorityDocuments.AsNoTracking().FirstAsync();
            Assert.Equal("ContainerData", doc.DocumentType);
            Assert.Equal(fileName, doc.ReferenceNumber);
            Assert.Contains("_inbox_content_hash", doc.PayloadJson);
        }
        finally
        {
            try { Directory.Delete(dropFolder, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task AuthorityDocInbox_DuplicateFileNameSkipped()
    {
        var dropFolder = Path.Combine(Path.GetTempPath(), "s24-inbox-" + Guid.NewGuid());
        var subFolder = Path.Combine(dropFolder, "ContainerData");
        Directory.CreateDirectory(subFolder);
        var fileName = "dup-container.json";
        var filePath = Path.Combine(subFolder, fileName);
        await File.WriteAllTextAsync(filePath,
            "{\"container_number\":\"MSCU0000005\"}");

        try
        {
            await SeedTenantAsync();
            await SeedExternalSystemAsync();
            await SeedCaseAsync(_locationId, "MSCU0000005");

            var optsMonitor = _sp.GetRequiredService<IOptions<IcumsFileScannerOptions>>();
            optsMonitor.Value.DropFolder = dropFolder;
            optsMonitor.Value.ExpectedSubfolders = new[] { "ContainerData" };

            var worker = _sp.GetRequiredService<AuthorityDocumentInboxWorker>();
            await InvokeAsync<int>(worker, "ScanOnceAsync");

            // Second scan — same filename + content. Worker dedupes.
            var ingested2 = await InvokeAsync<int>(worker, "ScanOnceAsync");
            Assert.Equal(0, ingested2);

            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var docs = await db.AuthorityDocuments.AsNoTracking().ToListAsync();
            Assert.Single(docs);
        }
        finally
        {
            try { Directory.Delete(dropFolder, recursive: true); } catch { /* best-effort */ }
        }
    }

    // -----------------------------------------------------------------
    // OutboundSubmissionDispatchWorker
    // -----------------------------------------------------------------

    [Fact]
    public async Task OutboundDispatch_AcceptedSubmission_MarkedAccepted()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        var caseId = await SeedCaseAsync(_locationId, "MSCU0000010");
        await SeedSubmissionAsync(caseId, "idem-accept", "pending");

        _esAdapter.NextSubmissionResult = new SubmissionResult(true, "{\"status\":\"ok\"}", null);

        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        var dispatched = await InvokeAsync<int>(worker, "DispatchOnceAsync");

        Assert.Equal(1, dispatched);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync();
        Assert.Equal("accepted", sub.Status);
        Assert.NotNull(sub.RespondedAt);
        Assert.NotNull(sub.LastAttemptAt);
    }

    [Fact]
    public async Task OutboundDispatch_RejectedSubmission_MarkedRejected()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        var caseId = await SeedCaseAsync(_locationId, "MSCU0000011");
        await SeedSubmissionAsync(caseId, "idem-reject", "pending");

        _esAdapter.NextSubmissionResult = new SubmissionResult(false, null, "Reason: invalid declaration");

        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        await InvokeAsync<int>(worker, "DispatchOnceAsync");

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync();
        Assert.Equal("rejected", sub.Status);
        Assert.Equal("Reason: invalid declaration", sub.ErrorMessage);
    }

    [Fact]
    public async Task OutboundDispatch_AdapterThrows_MarkedError()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        var caseId = await SeedCaseAsync(_locationId, "MSCU0000012");
        await SeedSubmissionAsync(caseId, "idem-throw", "pending");

        _esAdapter.ShouldThrowOnSubmit = true;

        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        await InvokeAsync<int>(worker, "DispatchOnceAsync");

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync();
        Assert.Equal("error", sub.Status);
        Assert.NotNull(sub.ErrorMessage);
    }

    [Fact]
    public async Task OutboundDispatch_PriorityOrdering_HighFirst()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        var caseId = await SeedCaseAsync(_locationId, "MSCU0000013");
        // Two pending submissions, one with higher priority. Verify
        // the high-priority one is dispatched first by checking the
        // RespondedAt ordering.
        await SeedSubmissionAsync(caseId, "idem-low", "pending", priority: 0);
        await SeedSubmissionAsync(caseId, "idem-high", "pending", priority: 5);

        _esAdapter.NextSubmissionResult = new SubmissionResult(true, "{}", null);

        var worker = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        await InvokeAsync<int>(worker, "DispatchOnceAsync");

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var subs = await db.OutboundSubmissions
            .AsNoTracking()
            .OrderBy(s => s.LastAttemptAt)
            .ToListAsync();
        Assert.Equal(2, subs.Count);
        Assert.Equal("idem-high", subs[0].IdempotencyKey);
    }

    // -----------------------------------------------------------------
    // OutboundSubmissionResultPollerWorker
    // -----------------------------------------------------------------

    [Fact]
    public async Task OutboundResultPoller_ConfirmedAuthorityResponse_ClosesSubmission()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        var caseId = await SeedCaseAsync(_locationId, "MSCU0000020");
        await SeedSubmissionAsync(caseId, "idem-poll-confirmed", "accepted", respondedAt: null);

        // Poller calls FetchDocumentsAsync — return one doc to signal
        // confirmation.
        _esAdapter.NextDocs = new[]
        {
            new AuthorityDocumentDto(
                _externalSystemInstanceId,
                "PollResult", "POLL-001",
                DateTimeOffset.UtcNow,
                "{\"final\":\"approved\"}")
        };

        var worker = _sp.GetRequiredService<OutboundSubmissionResultPollerWorker>();
        var polled = await InvokeAsync<int>(worker, "PollOnceAsync");

        Assert.Equal(1, polled);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync();
        Assert.NotNull(sub.RespondedAt);
        Assert.Equal("{\"final\":\"approved\"}", sub.ResponseJson);
    }

    [Fact]
    public async Task OutboundResultPoller_AuthorityStillPending_LeavesRespondedAtNull()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        var caseId = await SeedCaseAsync(_locationId, "MSCU0000021");
        await SeedSubmissionAsync(caseId, "idem-poll-pending", "accepted", respondedAt: null);

        // Adapter returns zero docs -> still pending.
        _esAdapter.NextDocs = System.Array.Empty<AuthorityDocumentDto>();

        var worker = _sp.GetRequiredService<OutboundSubmissionResultPollerWorker>();
        await InvokeAsync<int>(worker, "PollOnceAsync");

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var sub = await db.OutboundSubmissions.AsNoTracking().FirstAsync();
        Assert.Null(sub.RespondedAt);
        // LastAttemptAt should bump even when still pending so admin UI
        // can see we tried.
        Assert.NotNull(sub.LastAttemptAt);
    }

    // -----------------------------------------------------------------
    // AuthorityDocumentMatcherWorker
    // -----------------------------------------------------------------

    [Fact]
    public async Task AuthorityDocMatcher_ReattributesDocToBetterCase()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        // Case A: placeholder (no scans)
        var caseA = await SeedCaseAsync(_locationId, "PLACEHOLDER");
        // Case B: real scan in window
        var caseB = await SeedCaseAsync(_locationId, "MSCU0000030");

        // Doc was originally attributed to case A but its payload says
        // container = MSCU0000030. Matcher should re-point.
        await SeedAuthorityDocAsync(caseA, "{\"container_number\":\"MSCU0000030\"}", "BOE-MATCH-001",
            DateTimeOffset.UtcNow);

        var worker = _sp.GetRequiredService<AuthorityDocumentMatcherWorker>();
        var matched = await InvokeAsync<int>(worker, "MatchOnceAsync");

        Assert.Equal(1, matched);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var doc = await db.AuthorityDocuments.AsNoTracking().FirstAsync();
        Assert.Equal(caseB, doc.CaseId);
    }

    [Fact]
    public async Task AuthorityDocMatcher_AlreadyAttributedToBestMatch_NoOp()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        var caseId = await SeedCaseAsync(_locationId, "MSCU0000031");
        await SeedAuthorityDocAsync(caseId, "{\"container_number\":\"MSCU0000031\"}", "BOE-NOOP-001",
            DateTimeOffset.UtcNow);

        var worker = _sp.GetRequiredService<AuthorityDocumentMatcherWorker>();
        var matched = await InvokeAsync<int>(worker, "MatchOnceAsync");

        // Matcher reports "matched" only on re-attribution, not on
        // already-correct rows.
        Assert.Equal(0, matched);
    }

    [Fact]
    public async Task AuthorityDocMatcher_NoContainerNumber_NoMatch()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        var caseId = await SeedCaseAsync(_locationId, "MSCU0000032");
        await SeedAuthorityDocAsync(caseId, "{\"some_other_key\":\"x\"}", "BOE-NOCONT-001",
            DateTimeOffset.UtcNow);

        var worker = _sp.GetRequiredService<AuthorityDocumentMatcherWorker>();
        var matched = await InvokeAsync<int>(worker, "MatchOnceAsync");
        Assert.Equal(0, matched);
    }

    [Fact]
    public async Task AuthorityDocMatcher_OutsideCaptureWindow_NoMatch()
    {
        await SeedTenantAsync();
        await SeedExternalSystemAsync();
        var oldCaseId = await SeedCaseAsync(_locationId, "PLACEHOLDER-OLD");
        // Case B opened years ago — outside capture window.
        var farPastCase = Guid.NewGuid();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(_tenantId);
            db.Cases.Add(new InspectionCase
            {
                Id = farPastCase,
                LocationId = _locationId,
                SubjectType = CaseSubjectType.Container,
                SubjectIdentifier = "MSCU0000033",
                SubjectPayloadJson = "{}",
                State = InspectionWorkflowState.Open,
                OpenedAt = DateTimeOffset.UtcNow.AddYears(-1),
                StateEnteredAt = DateTimeOffset.UtcNow.AddYears(-1),
                TenantId = _tenantId
            });
            await db.SaveChangesAsync();
        }

        // Doc says MSCU0000033 but the only candidate case is way out
        // of window — matcher leaves the doc alone.
        await SeedAuthorityDocAsync(oldCaseId, "{\"container_number\":\"MSCU0000033\"}", "BOE-OUT-001",
            DateTimeOffset.UtcNow);

        var worker = _sp.GetRequiredService<AuthorityDocumentMatcherWorker>();
        var matched = await InvokeAsync<int>(worker, "MatchOnceAsync");
        Assert.Equal(0, matched);
    }

    // -----------------------------------------------------------------
    // Worker probes — basic IBackgroundServiceProbe smoke
    // -----------------------------------------------------------------

    [Fact]
    public void AllB3Workers_ImplementBackgroundServiceProbe()
    {
        var w1 = _sp.GetRequiredService<ScannerHealthSweepWorker>();
        var w2 = _sp.GetRequiredService<AseSyncWorker>();
        var w3 = _sp.GetRequiredService<AuthorityDocumentBackfillWorker>();
        var w4 = _sp.GetRequiredService<AuthorityDocumentInboxWorker>();
        var w5 = _sp.GetRequiredService<OutboundSubmissionDispatchWorker>();
        var w6 = _sp.GetRequiredService<OutboundSubmissionResultPollerWorker>();
        var w7 = _sp.GetRequiredService<AuthorityDocumentMatcherWorker>();

        // Each worker exposes a stable WorkerName and a snapshot.
        foreach (var worker in new NickERP.Platform.Telemetry.IBackgroundServiceProbe[] { w1, w2, w3, w4, w5, w6, w7 })
        {
            Assert.False(string.IsNullOrWhiteSpace(worker.WorkerName));
            var state = worker.GetState();
            Assert.NotNull(state);
        }
    }

    [Fact]
    public void AllB3WorkerOptions_DefaultDisabled()
    {
        // Reset to default values to assert the OOTB default for fresh
        // deploys is "Enabled = false" per Sprint 24 architectural
        // decision. We construct fresh option objects rather than
        // resolving from DI (which has been Configure'd above).
        Assert.False(new ScannerHealthSweepOptions().Enabled);
        Assert.False(new AseSyncOptions().Enabled);
        Assert.False(new IcumsApiPullOptions().Enabled);
        Assert.False(new IcumsFileScannerOptions().Enabled);
        Assert.False(new IcumsSubmissionDispatchOptions().Enabled);
        Assert.False(new IcumsSubmissionResultPollerOptions().Enabled);
        Assert.False(new ContainerDataMatcherOptions().Enabled);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task SeedTenantAsync()
    {
        using var scope = _sp.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        if (await tenancy.Tenants.AnyAsync(t => t.Id == _tenantId)) return;
        tenancy.Tenants.Add(new Tenant
        {
            Id = _tenantId,
            Code = "t1",
            Name = "Test Tenant",
            State = TenantState.Active,
            BillingPlan = "internal",
            TimeZone = "UTC",
            Locale = "en",
            Currency = "USD",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await tenancy.SaveChangesAsync();

        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);
        if (!await db.Locations.AnyAsync(l => l.Id == _locationId))
        {
            db.Locations.Add(new Location
            {
                Id = _locationId,
                Code = "loc1",
                Name = "Loc 1",
                TimeZone = "UTC",
                IsActive = true,
                TenantId = _tenantId
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task SeedScannerDeviceAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);
        db.ScannerDeviceInstances.Add(new ScannerDeviceInstance
        {
            Id = _scannerInstanceId,
            LocationId = _locationId,
            TypeCode = "test-scanner",
            DisplayName = "Test Scanner",
            ConfigJson = "{}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedCursorScannerDeviceAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);
        db.ScannerDeviceInstances.Add(new ScannerDeviceInstance
        {
            Id = _scannerInstanceId,
            LocationId = _locationId,
            TypeCode = "test-cursor-scanner",
            DisplayName = "Test Cursor Scanner",
            ConfigJson = "{}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedExternalSystemAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);
        if (await db.ExternalSystemInstances.AnyAsync(e => e.Id == _externalSystemInstanceId)) return;
        db.ExternalSystemInstances.Add(new ExternalSystemInstance
        {
            Id = _externalSystemInstanceId,
            TypeCode = "test-authority",
            DisplayName = "Test Authority",
            Description = "Test stub",
            Scope = ExternalSystemBindingScope.Shared,
            ConfigJson = "{}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCaseAsync(Guid locationId, string subjectIdentifier)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);
        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId,
            LocationId = locationId,
            SubjectType = CaseSubjectType.Container,
            SubjectIdentifier = subjectIdentifier,
            SubjectPayloadJson = "{}",
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
        return caseId;
    }

    private async Task SeedSubmissionAsync(
        Guid caseId, string idempotencyKey, string status, int priority = 0,
        DateTimeOffset? respondedAt = null)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);
        db.OutboundSubmissions.Add(new OutboundSubmission
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            ExternalSystemInstanceId = _externalSystemInstanceId,
            PayloadJson = "{}",
            IdempotencyKey = idempotencyKey,
            Status = status,
            SubmittedAt = DateTimeOffset.UtcNow,
            RespondedAt = respondedAt,
            Priority = priority,
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAuthorityDocAsync(
        Guid caseId, string payloadJson, string referenceNumber, DateTimeOffset receivedAt)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(_tenantId);
        db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            ExternalSystemInstanceId = _externalSystemInstanceId,
            DocumentType = "BOE",
            ReferenceNumber = referenceNumber,
            PayloadJson = payloadJson,
            ReceivedAt = receivedAt,
            TenantId = _tenantId
        });
        await db.SaveChangesAsync();
    }

    private static async Task<T> InvokeAsync<T>(object target, string methodName)
    {
        var method = target.GetType().GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task<T>)method!.Invoke(target, new object[] { CancellationToken.None })!;
        return await task;
    }
}

// -----------------------------------------------------------------
// Stub adapters & plugin registry
// -----------------------------------------------------------------

internal sealed class RecordingScannerAdapter : IScannerAdapter
{
    public string TypeCode => "test-scanner";
    public ScannerCapabilities Capabilities { get; } =
        new(SupportedFormats: new[] { "image/png" },
            SupportedModes: new[] { "single-energy" },
            SupportsLiveStream: true,
            SupportsDualEnergy: false);

    public NickERP.Inspection.Scanners.Abstractions.ConnectionTestResult NextTestResult { get; set; } =
        new(true, "ok");
    public bool ShouldThrowOnTest { get; set; }
    public int TestInvocationCount { get; private set; }

    public Task<NickERP.Inspection.Scanners.Abstractions.ConnectionTestResult> TestAsync(
        ScannerDeviceConfig config, CancellationToken ct = default)
    {
        TestInvocationCount++;
        if (ShouldThrowOnTest) throw new InvalidOperationException("simulated scanner offline");
        return Task.FromResult(NextTestResult);
    }

    public IAsyncEnumerable<RawScanArtifact> StreamAsync(ScannerDeviceConfig config, CancellationToken ct = default) =>
        EmptyStream();
    private static async IAsyncEnumerable<RawScanArtifact> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
    public Task<ParsedArtifact> ParseAsync(RawScanArtifact raw, CancellationToken ct = default) =>
        Task.FromResult(new ParsedArtifact(raw.DeviceId, raw.CapturedAt, 0, 0, 0, raw.Format, raw.Bytes,
            new Dictionary<string, string>()));
}

internal sealed class RecordingCursorAdapter : IScannerCursorSyncAdapter
{
    public string TypeCode => "test-cursor-scanner";
    public ScannerCapabilities Capabilities { get; } =
        new(SupportedFormats: new[] { "image/png" },
            SupportedModes: new[] { "single-energy" },
            SupportsLiveStream: false,
            SupportsDualEnergy: false);

    public CursorSyncBatch NextBatch { get; set; } =
        new(System.Array.Empty<CursorSyncRecord>(), string.Empty, false);
    public string? LastCursorSeen { get; private set; }
    public int PullInvocationCount { get; private set; }

    public Task<NickERP.Inspection.Scanners.Abstractions.ConnectionTestResult> TestAsync(
        ScannerDeviceConfig config, CancellationToken ct = default) =>
        Task.FromResult(new NickERP.Inspection.Scanners.Abstractions.ConnectionTestResult(true, "ok"));

    public IAsyncEnumerable<RawScanArtifact> StreamAsync(ScannerDeviceConfig config, CancellationToken ct = default) =>
        EmptyStream();
    private static async IAsyncEnumerable<RawScanArtifact> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
    public Task<ParsedArtifact> ParseAsync(RawScanArtifact raw, CancellationToken ct = default) =>
        Task.FromResult(new ParsedArtifact(raw.DeviceId, raw.CapturedAt, 0, 0, 0, raw.Format, raw.Bytes,
            new Dictionary<string, string>()));

    public Task<CursorSyncBatch> PullAsync(
        ScannerDeviceConfig config, string cursor, int batchLimit, CancellationToken ct)
    {
        PullInvocationCount++;
        LastCursorSeen = cursor;
        return Task.FromResult(NextBatch);
    }
}

internal sealed class RecordingExternalSystemAdapter : IExternalSystemAdapter
{
    public string TypeCode => "test-authority";
    public bool SupportsBulkFetch { get; set; } = true;
    public ExternalSystemCapabilities Capabilities =>
        new(SupportedDocumentTypes: new[] { "BOE", "PollResult" },
            SupportsPushNotifications: false,
            SupportsBulkFetch: SupportsBulkFetch,
            SupportsOutcomePull: false,
            SupportsOutcomePush: false);

    public IReadOnlyList<AuthorityDocumentDto> NextDocs { get; set; } = System.Array.Empty<AuthorityDocumentDto>();
    public SubmissionResult NextSubmissionResult { get; set; } = new(true, null, null);
    public bool ShouldThrowOnSubmit { get; set; }

    public Task<NickERP.Inspection.ExternalSystems.Abstractions.ConnectionTestResult> TestAsync(
        ExternalSystemConfig config, CancellationToken ct = default) =>
        Task.FromResult(new NickERP.Inspection.ExternalSystems.Abstractions.ConnectionTestResult(true, "ok"));

    public Task<IReadOnlyList<AuthorityDocumentDto>> FetchDocumentsAsync(
        ExternalSystemConfig config, CaseLookupCriteria lookup, CancellationToken ct = default) =>
        Task.FromResult(NextDocs);

    public Task<SubmissionResult> SubmitAsync(
        ExternalSystemConfig config, OutboundSubmissionRequest request, CancellationToken ct = default)
    {
        if (ShouldThrowOnSubmit) throw new InvalidOperationException("simulated authority crash");
        return Task.FromResult(NextSubmissionResult);
    }
}

/// <summary>
/// Minimal multi-plugin registry resolving the test stub adapters. Mirrors
/// the SinglePluginRegistry shape used in OutcomePullWorkerTests.
/// </summary>
internal sealed class MultiPluginRegistry : IPluginRegistry
{
    private readonly RecordingScannerAdapter _scanner;
    private readonly RecordingCursorAdapter _cursor;
    private readonly RecordingExternalSystemAdapter _es;

    public MultiPluginRegistry(
        RecordingScannerAdapter scanner,
        RecordingCursorAdapter cursor,
        RecordingExternalSystemAdapter es)
    {
        _scanner = scanner;
        _cursor = cursor;
        _es = es;
    }

    public IReadOnlyList<RegisteredPlugin> All { get; } = System.Array.Empty<RegisteredPlugin>();
    public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType) =>
        System.Array.Empty<RegisteredPlugin>();
    public RegisteredPlugin? FindByTypeCode(string module, string typeCode) => null;

    public T Resolve<T>(string module, string typeCode, IServiceProvider services) where T : class
    {
        if (typeof(T) == typeof(IScannerAdapter))
        {
            return string.Equals(typeCode, "test-scanner", StringComparison.OrdinalIgnoreCase)
                ? (T)(object)_scanner
                : string.Equals(typeCode, "test-cursor-scanner", StringComparison.OrdinalIgnoreCase)
                    ? (T)(object)_cursor
                    : throw new KeyNotFoundException($"no scanner plugin '{typeCode}'");
        }
        if (typeof(T) == typeof(IExternalSystemAdapter)
            && string.Equals(typeCode, "test-authority", StringComparison.OrdinalIgnoreCase))
        {
            return (T)(object)_es;
        }
        throw new KeyNotFoundException($"no plugin '{typeCode}' for type {typeof(T).Name}");
    }
}
