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
/// H1 — proves <see cref="PreRenderWorker.DrainOnceAsync"/> walks every
/// active tenant in <see cref="TenancyDbContext"/>, pushes the tenant
/// onto <see cref="ITenantContext"/> per cycle, and produces
/// <c>scan_render_artifacts</c> rows under each tenant without throwing.
///
/// <para>
/// Before H1, <see cref="ITenantContext"/> was unresolved when the
/// worker's per-cycle scope opened, so F1's
/// <c>TenantOwnedEntityInterceptor</c> threw on every
/// <c>SaveChanges</c> and (under Postgres) RLS returned zero rows. The
/// EF in-memory provider doesn't run RLS, but we DO register a real
/// <see cref="TenantContext"/> + a fake renderer that always succeeds —
/// so the assertion is equivalent: if the worker fails to call
/// <c>SetTenant</c> per cycle the seeded artifacts simply won't be
/// rendered (or will be rendered under the wrong tenant id).
/// </para>
/// </summary>
public sealed class PreRenderWorkerMultiTenantTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task DrainOnce_RendersOneArtifactPerTenant_WithCorrectTenantStamp()
    {
        var dbName = "imaging-multi-" + Guid.NewGuid();
        var sp = new ServiceCollection()
            .AddDbContext<InspectionDbContext>(o =>
                o.UseInMemoryDatabase(dbName)
                 .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
            .AddDbContext<TenancyDbContext>(o =>
                o.UseInMemoryDatabase("tenancy-" + dbName)
                 .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
            .AddScoped<ITenantContext, TenantContext>()
            .AddSingleton<IImageRenderer, FixedSuccessRenderer>()
            .AddSingleton<IImageStore, NullDiskImageStore>()
            .Configure<ImagingOptions>(o =>
            {
                o.StorageRoot = Path.Combine(Path.GetTempPath(), dbName);
                o.WorkerBatchSize = 8;
                o.MaxRenderAttempts = 3;
            })
            .AddSingleton<PreRenderWorker>(s => new PreRenderWorker(
                s, s.GetRequiredService<IOptions<ImagingOptions>>(), NullLogger<PreRenderWorker>.Instance))
            .BuildServiceProvider();

        var artifactT1Id = Guid.NewGuid();
        var artifactT2Id = Guid.NewGuid();

        // Seed: 2 active tenants + 1 ScanArtifact each.
        using (var scope = sp.CreateScope())
        {
            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            tenancy.Tenants.AddRange(
                new Tenant
                {
                    Id = 1, Code = "t1", Name = "Tenant One", IsActive = true,
                    BillingPlan = "internal", TimeZone = "UTC", Locale = "en", Currency = "USD",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new Tenant
                {
                    Id = 2, Code = "t2", Name = "Tenant Two", IsActive = true,
                    BillingPlan = "internal", TimeZone = "UTC", Locale = "en", Currency = "USD",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            await tenancy.SaveChangesAsync();

            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            db.ScanArtifacts.AddRange(
                new ScanArtifact
                {
                    Id = artifactT1Id,
                    ScanId = Guid.NewGuid(),
                    ArtifactKind = "Primary",
                    StorageUri = "test://t1",
                    MimeType = "image/png",
                    ContentHash = "tenant1hash",
                    MetadataJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    TenantId = 1
                },
                new ScanArtifact
                {
                    Id = artifactT2Id,
                    ScanId = Guid.NewGuid(),
                    ArtifactKind = "Primary",
                    StorageUri = "test://t2",
                    MimeType = "image/png",
                    ContentHash = "tenant2hash",
                    MetadataJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    TenantId = 2
                });
            await db.SaveChangesAsync();
        }

        var worker = sp.GetRequiredService<PreRenderWorker>();

        // DrainOnceAsync is the per-cycle entry point; reflection because
        // the method is private (matching the existing attempt-tracking
        // test's pattern).
        var drainOnce = typeof(PreRenderWorker).GetMethod("DrainOnceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        drainOnce.Should().NotBeNull("DrainOnceAsync is the per-cycle entry point");

        // No exception expected even though no scope-wide tenant has
        // been set — the worker's job is to set the tenant itself.
        var task = (Task<int>)drainOnce!.Invoke(worker, new object[] { CancellationToken.None })!;
        var produced = await task;

        // Each artifact gets thumbnail + preview = 2 derivatives × 2 tenants = 4.
        produced.Should().Be(4, "two derivative kinds per artifact, one artifact per tenant");

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var renders = await db.ScanRenderArtifacts.AsNoTracking().ToListAsync();
            renders.Should().HaveCount(4, "two kinds per artifact under each tenant");

            var byTenant = renders.GroupBy(r => r.TenantId).ToDictionary(g => g.Key, g => g.ToList());
            byTenant.Should().ContainKey(1L);
            byTenant.Should().ContainKey(2L);

            byTenant[1].Select(r => r.ScanArtifactId).Should().OnlyContain(id => id == artifactT1Id,
                "tenant 1's render rows must reference tenant 1's artifact");
            byTenant[2].Select(r => r.ScanArtifactId).Should().OnlyContain(id => id == artifactT2Id,
                "tenant 2's render rows must reference tenant 2's artifact");
        }

        // No render-attempt failure rows should have been written —
        // the renderer always succeeds and the interceptor must NOT
        // throw for any of the 4 SaveChanges calls.
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            (await db.ScanRenderAttempts.AsNoTracking().ToListAsync())
                .Should().BeEmpty("a successful drain should produce no failure-tracking rows");
        }
    }

    /// <summary>
    /// Always-succeeding renderer — returns a 1×1 PNG-shaped buffer.
    /// </summary>
    private sealed class FixedSuccessRenderer : IImageRenderer
    {
        private static readonly byte[] PngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public Task<RenderedImage> RenderThumbnailAsync(ReadOnlyMemory<byte> sourceBytes, CancellationToken ct = default)
            => Task.FromResult(new RenderedImage(PngBytes, 256, 256, "image/png"));

        public Task<RenderedImage> RenderPreviewAsync(ReadOnlyMemory<byte> sourceBytes, CancellationToken ct = default)
            => Task.FromResult(new RenderedImage(PngBytes, 1024, 1024, "image/png"));
    }

    /// <summary>
    /// In-memory <see cref="IImageStore"/> — returns a fixed source-bytes
    /// payload and no-ops on save. Lets the worker's drain logic exercise
    /// the SaveChanges path without touching disk.
    /// </summary>
    private sealed class NullDiskImageStore : IImageStore
    {
        public Task<string> SaveSourceAsync(string contentHash, string fileExtension, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
            => Task.FromResult($"test://source/{contentHash}");
        public Task<byte[]> ReadSourceAsync(string contentHash, string fileExtension, CancellationToken ct = default)
            => Task.FromResult(new byte[] { 0x00, 0x01, 0x02, 0x03 });
        public Task<string> SaveRenderAsync(Guid scanArtifactId, string kind, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
            => Task.FromResult($"test://render/{scanArtifactId}/{kind}");
        public Stream? OpenRenderRead(Guid scanArtifactId, string kind) => null;
    }
}
