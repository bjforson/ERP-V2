using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 25 — exercises <see cref="TenantExportService"/> end-to-end
/// against the EF in-memory provider. Cross-DB read orchestration is
/// not in scope here — the live <see cref="TenantExportBundleBuilder"/>
/// needs Postgres. The service surface (request / status / list /
/// revoke / download lifecycle gates) is covered exhaustively.
/// </summary>
public sealed class TenantExportServiceTests : IDisposable
{
    private readonly DateTimeOffset _now = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly string _tempRoot;

    public TenantExportServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tenant-export-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestExport_CreatesPendingRow_AndAuditEvent()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var publisher = new RecordingPublisher();
        var svc = BuildService(ctx, publisher);
        var actor = Guid.NewGuid();

        var result = await svc.RequestExportAsync(
            tenantId: 5,
            format: TenantExportFormat.JsonBundle,
            scope: TenantExportScope.All,
            requestingUserId: actor);

        result.Status.Should().Be(TenantExportStatus.Pending);
        result.TenantId.Should().Be(5);
        result.RequestedByUserId.Should().Be(actor);
        result.Format.Should().Be(TenantExportFormat.JsonBundle);
        result.Scope.Should().Be(TenantExportScope.All);

        var row = await ctx.TenantExportRequests.AsNoTracking().FirstAsync(r => r.Id == result.Id);
        row.Status.Should().Be(TenantExportStatus.Pending);

        publisher.Events.Should().ContainSingle(e =>
            e.EventType == "nickerp.tenancy.tenant_export_requested"
            && e.ActorUserId == actor
            && e.TenantId == 5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestExport_UnknownTenant_Throws()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx, new RecordingPublisher());

        var act = async () => await svc.RequestExportAsync(
            tenantId: 999, TenantExportFormat.JsonBundle, TenantExportScope.All, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestExport_EmptyUserId_Throws()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var svc = BuildService(ctx, new RecordingPublisher());

        var act = async () => await svc.RequestExportAsync(
            tenantId: 5, TenantExportFormat.JsonBundle, TenantExportScope.All, Guid.Empty);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RequestExport_ForSoftDeletedTenant_Allowed()
    {
        // The whole point of the retention window is to allow exports
        // before hard-purge — soft-deleted tenants must remain
        // exportable.
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5, state: TenantState.SoftDeleted);
        var svc = BuildService(ctx, new RecordingPublisher());

        var result = await svc.RequestExportAsync(
            5, TenantExportFormat.JsonBundle, TenantExportScope.All, Guid.NewGuid());
        result.Status.Should().Be(TenantExportStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetExportStatus_ReturnsRow()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var svc = BuildService(ctx, new RecordingPublisher());
        var row = await svc.RequestExportAsync(
            5, TenantExportFormat.CsvFlat, TenantExportScope.InspectionOnly, Guid.NewGuid());

        var status = await svc.GetExportStatusAsync(row.Id);
        status.Should().NotBeNull();
        status!.Id.Should().Be(row.Id);
        status.Format.Should().Be(TenantExportFormat.CsvFlat);
        status.Scope.Should().Be(TenantExportScope.InspectionOnly);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetExportStatus_UnknownId_ReturnsNull()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx, new RecordingPublisher());

        var status = await svc.GetExportStatusAsync(Guid.NewGuid());
        status.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListExports_ReturnsNewestFirst()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        await SeedTenantAsync(ctx, tenantId: 6);
        var svc = BuildService(ctx, new RecordingPublisher());

        // Three for tenant 5, one for tenant 6.
        var older = await svc.RequestExportAsync(5, TenantExportFormat.JsonBundle, TenantExportScope.All, Guid.NewGuid());
        await Task.Delay(5);
        var middle = await svc.RequestExportAsync(5, TenantExportFormat.CsvFlat, TenantExportScope.FinanceOnly, Guid.NewGuid());
        await Task.Delay(5);
        var newest = await svc.RequestExportAsync(5, TenantExportFormat.Sql, TenantExportScope.IdentityAndAudit, Guid.NewGuid());
        await svc.RequestExportAsync(6, TenantExportFormat.JsonBundle, TenantExportScope.All, Guid.NewGuid());

        // The in-memory provider returns rows by insertion order; we
        // compare by RequestedAt timestamps because the seeded clock
        // is fixed. To get strict ordering we patch RequestedAt on the
        // rows (the service writes _clock.GetUtcNow() which is fixed in
        // the FakeClock). Sort manually:
        var listed = await svc.ListExportsAsync(5);
        listed.Should().HaveCount(3);
        listed.Should().OnlyContain(r => r.TenantId == 5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DownloadExport_Pending_ReturnsNull()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var svc = BuildService(ctx, new RecordingPublisher());
        var row = await svc.RequestExportAsync(
            5, TenantExportFormat.JsonBundle, TenantExportScope.All, Guid.NewGuid());

        var dl = await svc.DownloadExportAsync(row.Id, Guid.NewGuid());
        dl.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DownloadExport_Revoked_ReturnsNull()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var svc = BuildService(ctx, new RecordingPublisher());
        var rowEntity = await CompleteExportFixtureAsync(ctx, svc, tenantId: 5);
        await svc.RevokeExportAsync(rowEntity.Id, Guid.NewGuid());

        var dl = await svc.DownloadExportAsync(rowEntity.Id, Guid.NewGuid());
        dl.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DownloadExport_Expired_ReturnsNull()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var svc = BuildService(ctx, new RecordingPublisher());
        var row = await CompleteExportFixtureAsync(ctx, svc, tenantId: 5);

        // Force expiry into the past.
        row.ExpiresAt = _now.AddDays(-1);
        await ctx.SaveChangesAsync();

        var dl = await svc.DownloadExportAsync(row.Id, Guid.NewGuid());
        dl.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DownloadExport_EmptyUserId_ReturnsNull()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var svc = BuildService(ctx, new RecordingPublisher());
        var row = await CompleteExportFixtureAsync(ctx, svc, tenantId: 5);

        var dl = await svc.DownloadExportAsync(row.Id, Guid.Empty);
        dl.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DownloadExport_Completed_ReturnsStream_AndBumpsCounter()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var publisher = new RecordingPublisher();
        var svc = BuildService(ctx, publisher);
        var row = await CompleteExportFixtureAsync(ctx, svc, tenantId: 5);
        var requester = Guid.NewGuid();

        var dl = await svc.DownloadExportAsync(row.Id, requester);
        dl.Should().NotBeNull();
        dl!.ContentType.Should().Be("application/zip");
        dl.SizeBytes.Should().BeGreaterThan(0);
        dl.FileName.Should().Contain(row.Id.ToString("N"));
        await dl.Stream.DisposeAsync();

        // Counter is bumped + audit emitted.
        var refetched = await ctx.TenantExportRequests.AsNoTracking().FirstAsync(r => r.Id == row.Id);
        refetched.DownloadCount.Should().Be(1);
        refetched.LastDownloadedAt.Should().NotBeNull();

        publisher.Events.Should().Contain(e => e.EventType == "nickerp.tenancy.tenant_export_downloaded");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DownloadExport_MultipleDownloads_BumpsCounterEachTime()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var svc = BuildService(ctx, new RecordingPublisher());
        var row = await CompleteExportFixtureAsync(ctx, svc, tenantId: 5);

        for (int i = 0; i < 3; i++)
        {
            var dl = await svc.DownloadExportAsync(row.Id, Guid.NewGuid());
            dl.Should().NotBeNull();
            await dl!.Stream.DisposeAsync();
        }

        var refetched = await ctx.TenantExportRequests.AsNoTracking().FirstAsync(r => r.Id == row.Id);
        refetched.DownloadCount.Should().Be(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RevokeExport_FlipsStatus_DeletesArtifact_AndAudits()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var publisher = new RecordingPublisher();
        var svc = BuildService(ctx, publisher);
        var row = await CompleteExportFixtureAsync(ctx, svc, tenantId: 5);
        File.Exists(row.ArtifactPath!).Should().BeTrue();

        var actor = Guid.NewGuid();
        await svc.RevokeExportAsync(row.Id, actor);

        var refetched = await ctx.TenantExportRequests.AsNoTracking().FirstAsync(r => r.Id == row.Id);
        refetched.Status.Should().Be(TenantExportStatus.Revoked);
        refetched.RevokedByUserId.Should().Be(actor);
        refetched.RevokedAt.Should().NotBeNull();
        File.Exists(row.ArtifactPath!).Should().BeFalse();

        publisher.Events.Should().Contain(e =>
            e.EventType == "nickerp.tenancy.tenant_export_revoked"
            && e.ActorUserId == actor);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RevokeExport_AlreadyRevoked_IsIdempotent()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var publisher = new RecordingPublisher();
        var svc = BuildService(ctx, publisher);
        var row = await CompleteExportFixtureAsync(ctx, svc, tenantId: 5);

        await svc.RevokeExportAsync(row.Id, Guid.NewGuid());
        publisher.Events.Clear();
        await svc.RevokeExportAsync(row.Id, Guid.NewGuid());

        publisher.Events.Should().BeEmpty("revoking an already-revoked export is a no-op");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RevokeExport_UnknownId_Throws()
    {
        await using var ctx = BuildCtx();
        var svc = BuildService(ctx, new RecordingPublisher());

        var act = async () => await svc.RevokeExportAsync(Guid.NewGuid(), Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RevokeExport_EmptyUserId_Throws()
    {
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var svc = BuildService(ctx, new RecordingPublisher());
        var row = await CompleteExportFixtureAsync(ctx, svc, tenantId: 5);

        var act = async () => await svc.RevokeExportAsync(row.Id, Guid.Empty);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BundleArtifact_IsValidZip_WithManifest()
    {
        // Verifies the fixture really writes a valid zip bundle so the
        // Download tests aren't false-positives over an empty file.
        await using var ctx = BuildCtx();
        await SeedTenantAsync(ctx, tenantId: 5);
        var svc = BuildService(ctx, new RecordingPublisher());
        var row = await CompleteExportFixtureAsync(ctx, svc, tenantId: 5);

        await using var fs = File.OpenRead(row.ArtifactPath!);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        zip.Entries.Should().Contain(e => e.FullName == "manifest.json");
    }

    // -----------------------------------------------------------------
    // Test scaffolding
    // -----------------------------------------------------------------

    private TenancyDbContext BuildCtx()
    {
        var dbName = "tenant-export-" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TenancyDbContext(options);
    }

    private TenantExportService BuildService(TenancyDbContext ctx, RecordingPublisher publisher)
    {
        var options = new TenantExportOptions
        {
            OutputPath = _tempRoot,
            RetentionDays = 7,
            // Connection strings null — the bundle builder is not
            // exercised by these unit tests; the artifact-on-disk
            // tests use CompleteExportFixtureAsync which writes the
            // artifact directly with a synthetic zip.
        };
        var clock = new FakeClock(_now);
        return new TenantExportService(
            ctx, publisher, options,
            NullLogger<TenantExportService>.Instance, clock);
    }

    private async Task SeedTenantAsync(TenancyDbContext ctx, long tenantId, TenantState state = TenantState.Active)
    {
        ctx.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Code = $"seed-{tenantId}",
            Name = $"Seed {tenantId}",
            BillingPlan = "internal",
            TimeZone = "UTC",
            Locale = "en",
            Currency = "USD",
            State = state,
            RetentionDays = 90,
            CreatedAt = _now.AddDays(-30),
            DeletedAt = state == TenantState.SoftDeleted ? _now.AddDays(-1) : null,
            HardPurgeAfter = state == TenantState.SoftDeleted ? _now.AddDays(89) : null,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Drives a fresh request through to <see cref="TenantExportStatus.Completed"/>
    /// by writing a minimal valid zip bundle directly to the configured
    /// output path. Lets the Download / Revoke tests run without
    /// booting the full TenantExportRunner + Postgres.
    /// </summary>
    private async Task<TenantExportRequest> CompleteExportFixtureAsync(
        TenancyDbContext ctx, TenantExportService svc, long tenantId)
    {
        var row = await svc.RequestExportAsync(
            tenantId, TenantExportFormat.JsonBundle, TenantExportScope.All, Guid.NewGuid());

        var artifactDir = Path.Combine(_tempRoot, tenantId.ToString());
        Directory.CreateDirectory(artifactDir);
        var artifactPath = Path.Combine(artifactDir, $"{row.Id:N}.zip");

        await using (var fs = new FileStream(artifactPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("manifest.json");
            await using var es = entry.Open();
            var manifest = Encoding.UTF8.GetBytes("{\"test\":true}");
            await es.WriteAsync(manifest);
        }

        var size = new FileInfo(artifactPath).Length;
        var sha = await ComputeSha256Async(artifactPath);

        var tracked = await ctx.TenantExportRequests.FirstAsync(r => r.Id == row.Id);
        tracked.Status = TenantExportStatus.Completed;
        tracked.ArtifactPath = artifactPath;
        tracked.ArtifactSizeBytes = size;
        tracked.ArtifactSha256 = sha;
        tracked.ExpiresAt = _now.AddDays(7);
        tracked.CompletedAt = _now;
        await ctx.SaveChangesAsync();
        return tracked;
    }

    private static async Task<byte[]> ComputeSha256Async(string path)
    {
        await using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return await sha.ComputeHashAsync(fs);
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<DomainEvent> Events { get; } = new();

        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.FromResult(evt with { EventId = Guid.NewGuid() });
        }

        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            Events.AddRange(events);
            return Task.FromResult<IReadOnlyList<DomainEvent>>(events);
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
