using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Imaging;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Phase F5 — verifies <see cref="SourceJanitorWorker"/> evicts source
/// blobs whose only-referencing case is closed (or cancelled) and is
/// older than <c>ImagingOptions.SourceRetentionDays</c>.
///
/// Uses the EF in-memory provider plus a real on-disk
/// <see cref="DiskImageStore"/> rooted in a temp directory; the
/// directory is cleaned up at the end of the test.
/// </summary>
public sealed class SourceJanitorWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "janitor-" + Guid.NewGuid());

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SweepOnce_DeletesSourceBlob_WhenCaseClosedAndOlderThanRetention()
    {
        var dbName = "janitor-" + Guid.NewGuid();
        var sp = new ServiceCollection()
            .AddDbContext<InspectionDbContext>(o =>
                o.UseInMemoryDatabase(dbName)
                 .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
            // H1 — SourceJanitorWorker now walks tenancy.tenants per
            // cycle, so the harness must register TenancyDbContext +
            // ITenantContext.
            .AddDbContext<TenancyDbContext>(o =>
                o.UseInMemoryDatabase("tenancy-" + dbName)
                 .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
            .AddScoped<ITenantContext, TenantContext>()
            .AddSingleton<IImageStore, DiskImageStore>()
            .Configure<ImagingOptions>(o =>
            {
                o.StorageRoot = _root;
                o.SourceRetentionDays = 30;
            })
            .AddSingleton(sp1 => new SourceJanitorWorker(
                sp1, sp1.GetRequiredService<IOptions<ImagingOptions>>(),
                NullLogger<SourceJanitorWorker>.Instance))
            .BuildServiceProvider();

        // Persist a synthetic source blob so the janitor has something
        // to delete. Content-addressed path layout: source/{hash[0..2]}/{hash}.png.
        var hash = "abc1234567890def1234567890abcd1234567890";
        var store = sp.GetRequiredService<IImageStore>();
        await store.SaveSourceAsync(hash, ".png", new byte[] { 1, 2, 3 });
        var disk = (DiskImageStore)store;
        var blobPath = disk.GetSourcePath(hash, ".png");
        File.Exists(blobPath).Should().BeTrue("blob saved to disk by SaveSourceAsync");

        // Seed: one active tenant + a closed case with a scan and an
        // artifact pointing at the blob, with CreatedAt older than the
        // retention cutoff.
        using (var scope = sp.CreateScope())
        {
            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            tenancy.Tenants.Add(new Tenant
            {
                Id = 1, Code = "t1", Name = "Tenant 1", State = TenantState.Active,
                BillingPlan = "internal", TimeZone = "UTC", Locale = "en", Currency = "USD",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await tenancy.SaveChangesAsync();

            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var caseId = Guid.NewGuid();
            var locationId = Guid.NewGuid();
            db.Locations.Add(new Location
            {
                Id = locationId,
                Code = "T1", Name = "Test", TimeZone = "UTC", IsActive = true, TenantId = 1
            });
            db.Cases.Add(new InspectionCase
            {
                Id = caseId,
                LocationId = locationId,
                SubjectType = CaseSubjectType.Container,
                SubjectIdentifier = "TESTU0000001",
                State = InspectionWorkflowState.Closed,
                OpenedAt = DateTimeOffset.UtcNow.AddDays(-90),
                StateEnteredAt = DateTimeOffset.UtcNow.AddDays(-60),
                TenantId = 1
            });
            var scanId = Guid.NewGuid();
            db.Scans.Add(new Scan
            {
                Id = scanId,
                CaseId = caseId,
                ScannerDeviceInstanceId = Guid.NewGuid(),
                IdempotencyKey = "test-key-" + scanId,
                CapturedAt = DateTimeOffset.UtcNow.AddDays(-90),
                TenantId = 1
            });
            db.ScanArtifacts.Add(new ScanArtifact
            {
                Id = Guid.NewGuid(),
                ScanId = scanId,
                ArtifactKind = "Primary",
                StorageUri = blobPath,
                MimeType = "image/png",
                ContentHash = hash,
                MetadataJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-90), // well past retention
                TenantId = 1
            });
            await db.SaveChangesAsync();
        }

        var janitor = sp.GetRequiredService<SourceJanitorWorker>();
        var deleted = await janitor.SweepOnceAsync(CancellationToken.None);

        deleted.Should().Be(1, "the closed-case artifact was past retention and uniquely held the blob");
        File.Exists(blobPath).Should().BeFalse("the source blob should be evicted from disk");

        // Row stays — only the blob is evicted in this version.
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            (await db.ScanArtifacts.CountAsync()).Should().Be(1,
                "the janitor only evicts blobs; ScanArtifact rows remain for audit replay");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SweepOnce_DoesNotDelete_WhenAnotherOpenCaseSharesBlob()
    {
        var dbName = "janitor-share-" + Guid.NewGuid();
        var sp = new ServiceCollection()
            .AddDbContext<InspectionDbContext>(o =>
                o.UseInMemoryDatabase(dbName)
                 .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
            // H1 — see sibling test for rationale.
            .AddDbContext<TenancyDbContext>(o =>
                o.UseInMemoryDatabase("tenancy-" + dbName)
                 .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
            .AddScoped<ITenantContext, TenantContext>()
            .AddSingleton<IImageStore, DiskImageStore>()
            .Configure<ImagingOptions>(o =>
            {
                o.StorageRoot = _root;
                o.SourceRetentionDays = 30;
            })
            .AddSingleton(sp1 => new SourceJanitorWorker(
                sp1, sp1.GetRequiredService<IOptions<ImagingOptions>>(),
                NullLogger<SourceJanitorWorker>.Instance))
            .BuildServiceProvider();

        var hash = "shared9999999999999999999999999999999999";
        var store = sp.GetRequiredService<IImageStore>();
        await store.SaveSourceAsync(hash, ".png", new byte[] { 9 });
        var disk = (DiskImageStore)store;
        var blobPath = disk.GetSourcePath(hash, ".png");

        using (var scope = sp.CreateScope())
        {
            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            tenancy.Tenants.Add(new Tenant
            {
                Id = 1, Code = "t1", Name = "Tenant 1", State = TenantState.Active,
                BillingPlan = "internal", TimeZone = "UTC", Locale = "en", Currency = "USD",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await tenancy.SaveChangesAsync();

            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var locationId = Guid.NewGuid();
            db.Locations.Add(new Location
            {
                Id = locationId, Code = "T1", Name = "Test", TimeZone = "UTC",
                IsActive = true, TenantId = 1
            });

            // Closed + old case
            var closedCaseId = Guid.NewGuid();
            db.Cases.Add(new InspectionCase
            {
                Id = closedCaseId, LocationId = locationId,
                SubjectType = CaseSubjectType.Container, SubjectIdentifier = "OLD0000000001",
                State = InspectionWorkflowState.Closed,
                OpenedAt = DateTimeOffset.UtcNow.AddDays(-90),
                StateEnteredAt = DateTimeOffset.UtcNow.AddDays(-60),
                TenantId = 1
            });
            // Open case sharing the same blob
            var openCaseId = Guid.NewGuid();
            db.Cases.Add(new InspectionCase
            {
                Id = openCaseId, LocationId = locationId,
                SubjectType = CaseSubjectType.Container, SubjectIdentifier = "OPEN000000001",
                State = InspectionWorkflowState.Open,
                OpenedAt = DateTimeOffset.UtcNow.AddDays(-1),
                StateEnteredAt = DateTimeOffset.UtcNow.AddDays(-1),
                TenantId = 1
            });

            foreach (var (caseId, age) in new[] { (closedCaseId, -90), (openCaseId, -1) })
            {
                var scanId = Guid.NewGuid();
                db.Scans.Add(new Scan
                {
                    Id = scanId, CaseId = caseId, ScannerDeviceInstanceId = Guid.NewGuid(),
                    IdempotencyKey = "k-" + scanId,
                    CapturedAt = DateTimeOffset.UtcNow.AddDays(age), TenantId = 1
                });
                db.ScanArtifacts.Add(new ScanArtifact
                {
                    Id = Guid.NewGuid(), ScanId = scanId, ArtifactKind = "Primary",
                    StorageUri = blobPath, MimeType = "image/png", ContentHash = hash,
                    MetadataJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(age), TenantId = 1
                });
            }
            await db.SaveChangesAsync();
        }

        var janitor = sp.GetRequiredService<SourceJanitorWorker>();
        var deleted = await janitor.SweepOnceAsync(CancellationToken.None);

        deleted.Should().Be(0, "another open case still references this blob");
        File.Exists(blobPath).Should().BeTrue("blob must remain because an open case still references it");
    }
}
