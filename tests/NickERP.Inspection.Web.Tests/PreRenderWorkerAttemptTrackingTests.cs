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
/// Phase F5 — verifies the <c>PreRenderWorker</c>'s attempt-count
/// retry guard: after <c>ImagingOptions.MaxRenderAttempts</c> failures
/// for a given (artifact, kind), the worker stamps
/// <c>PermanentlyFailedAt</c> and stops retrying.
///
/// Uses EF Core in-memory provider (Docker is unavailable in this
/// environment) plus a deliberately-throwing <c>IImageRenderer</c>; the
/// drain query and the failure-recording path don't depend on
/// Postgres-specific behaviour, so the in-memory provider is sufficient.
/// </summary>
public sealed class PreRenderWorkerAttemptTrackingTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task PoisonArtifact_StopsRetrying_AfterMaxAttempts()
    {
        var dbName = "imaging-attempts-" + Guid.NewGuid();
        var tenancyDbName = "tenancy-" + dbName;
        var sp = new ServiceCollection()
            .AddDbContext<InspectionDbContext>(o =>
                o.UseInMemoryDatabase(dbName)
                 .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
            // H1 — PreRenderWorker now walks tenancy.tenants per cycle, so
            // the test harness has to register TenancyDbContext + a
            // resolved ITenantContext or the worker silently no-ops.
            .AddDbContext<TenancyDbContext>(o =>
                o.UseInMemoryDatabase(tenancyDbName)
                 .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
            .AddScoped<ITenantContext, TenantContext>()
            .AddSingleton<IImageRenderer, ThrowingRenderer>()
            .AddSingleton<IImageStore, ReturnGarbageImageStore>()
            .Configure<ImagingOptions>(o =>
            {
                o.StorageRoot = Path.Combine(Path.GetTempPath(), dbName);
                o.WorkerBatchSize = 8;
                o.MaxRenderAttempts = 5;
            })
            .AddSingleton<PreRenderWorker>(s => new PreRenderWorker(
                s, s.GetRequiredService<IOptions<ImagingOptions>>(), NullLogger<PreRenderWorker>.Instance))
            .BuildServiceProvider();

        // Seed: one active tenant + one ScanArtifact whose render will
        // always throw.
        using (var scope = sp.CreateScope())
        {
            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            tenancy.Tenants.Add(new Tenant
            {
                Id = 1, Code = "t1", Name = "Tenant 1", IsActive = true,
                BillingPlan = "internal", TimeZone = "UTC", Locale = "en", Currency = "USD",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await tenancy.SaveChangesAsync();

            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            db.ScanArtifacts.Add(new ScanArtifact
            {
                Id = Guid.NewGuid(),
                ScanId = Guid.NewGuid(),
                ArtifactKind = "Primary",
                StorageUri = "test://x",
                MimeType = "image/png",
                ContentHash = "deadbeef",
                MetadataJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                TenantId = 1
            });
            await db.SaveChangesAsync();
        }

        var worker = sp.GetRequiredService<PreRenderWorker>();

        // Drive the worker through 6 sweep cycles. Each cycle should
        // bump AttemptCount by 1 per kind (thumbnail + preview).
        // After 5 failures the worker stamps PermanentlyFailedAt
        // and the 6th sweep finds nothing to do.
        for (int i = 0; i < 6; i++)
        {
            var drainOnce = typeof(PreRenderWorker).GetMethod("DrainOnceAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            drainOnce.Should().NotBeNull("DrainOnceAsync is the per-cycle entry point");
            var task = (Task<int>)drainOnce!.Invoke(worker, new object[] { CancellationToken.None })!;
            await task;
        }

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
            var attempts = await db.ScanRenderAttempts.ToListAsync();
            attempts.Should().HaveCount(2, "one row per kind: thumbnail + preview");

            foreach (var a in attempts)
            {
                a.AttemptCount.Should().Be(5, "MaxRenderAttempts bound caps the counter at the threshold");
                a.PermanentlyFailedAt.Should().NotBeNull(
                    "the worker must stop retrying poison messages once AttemptCount >= MaxRenderAttempts");
                a.LastError.Should().NotBeNullOrEmpty();
            }

            var renders = await db.ScanRenderArtifacts.ToListAsync();
            renders.Should().BeEmpty("no successful renders when the renderer always throws");
        }
    }

    private sealed class ThrowingRenderer : IImageRenderer
    {
        public Task<RenderedImage> RenderThumbnailAsync(ReadOnlyMemory<byte> sourceBytes, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated render failure");
        public Task<RenderedImage> RenderPreviewAsync(ReadOnlyMemory<byte> sourceBytes, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated render failure");
    }

    /// <summary>
    /// Returns a fixed byte buffer regardless of hash — the worker
    /// only needs ReadSourceAsync to succeed; the renderer is the one
    /// that throws.
    /// </summary>
    private sealed class ReturnGarbageImageStore : IImageStore
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
