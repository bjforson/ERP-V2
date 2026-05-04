using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Database.Workers;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 25 — exercises the runner's pickup / sweep / orphan-adoption
/// logic against the EF in-memory provider. The actual bundle build is
/// not exercised here (it needs Postgres); we drive the runner with
/// connection strings empty so each pick-up immediately succeeds with
/// a manifest-only bundle.
/// </summary>
public sealed class TenantExportRunnerTests : IDisposable
{
    private readonly DateTimeOffset _now = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly string _tempRoot;

    public TenantExportRunnerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tenant-export-runner-tests-" + Guid.NewGuid().ToString("N"));
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
            /* best-effort */
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TickAsync_PicksPendingRow_TransitionsToCompleted()
    {
        var (sp, dbName) = BuildScopeFactory();
        await SeedRequestAsync(sp, tenantId: 5, status: TenantExportStatus.Pending);
        var runner = BuildRunner(sp);

        await runner.TickAsync(CancellationToken.None);

        await using var ctx = NewCtx(dbName);
        var row = await ctx.TenantExportRequests.AsNoTracking().FirstAsync();
        row.Status.Should().Be(TenantExportStatus.Completed);
        row.ArtifactPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(row.ArtifactPath!).Should().BeTrue();
        row.ArtifactSizeBytes.Should().BeGreaterThan(0);
        row.ArtifactSha256.Should().NotBeNull();
        row.ArtifactSha256!.Length.Should().Be(32);
        row.CompletedAt.Should().NotBeNull();
        row.ExpiresAt.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TickAsync_RespectsConcurrencyLimit()
    {
        var (sp, dbName) = BuildScopeFactory();
        // Seed five Pending rows; cap at 2 per tick.
        for (int i = 1; i <= 5; i++)
        {
            await SeedRequestAsync(sp, tenantId: i, status: TenantExportStatus.Pending);
        }
        var runner = BuildRunner(sp, maxConcurrent: 2);

        // Single tick — should pick exactly two.
        await runner.TickAsync(CancellationToken.None);

        await using var ctx = NewCtx(dbName);
        var completed = await ctx.TenantExportRequests
            .AsNoTracking()
            .Where(r => r.Status == TenantExportStatus.Completed)
            .CountAsync();
        var pending = await ctx.TenantExportRequests
            .AsNoTracking()
            .Where(r => r.Status == TenantExportStatus.Pending)
            .CountAsync();
        completed.Should().Be(2);
        pending.Should().Be(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TickAsync_NoPendingRows_NoOp()
    {
        var (sp, dbName) = BuildScopeFactory();
        var runner = BuildRunner(sp);

        await runner.TickAsync(CancellationToken.None);

        await using var ctx = NewCtx(dbName);
        var any = await ctx.TenantExportRequests.AnyAsync();
        any.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SweepExpiredAsync_FlipsCompletedToExpired_AndDeletesArtifact()
    {
        var (sp, dbName) = BuildScopeFactory();
        var runner = BuildRunner(sp);

        // Seed a Completed row with ExpiresAt in the past + a real
        // file on disk.
        var artifactPath = Path.Combine(_tempRoot, "expired.zip");
        await CreateMinimalZipAsync(artifactPath);
        var id = await SeedCompletedAsync(sp,
            tenantId: 5,
            artifactPath: artifactPath,
            expiresAt: _now.AddDays(-1));

        await runner.SweepExpiredAsync(CancellationToken.None);

        await using var ctx = NewCtx(dbName);
        var row = await ctx.TenantExportRequests.AsNoTracking().FirstAsync(r => r.Id == id);
        row.Status.Should().Be(TenantExportStatus.Expired);
        File.Exists(artifactPath).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SweepExpiredAsync_NotYetExpired_Skipped()
    {
        var (sp, dbName) = BuildScopeFactory();
        var runner = BuildRunner(sp);

        var artifactPath = Path.Combine(_tempRoot, "active.zip");
        await CreateMinimalZipAsync(artifactPath);
        var id = await SeedCompletedAsync(sp,
            tenantId: 5,
            artifactPath: artifactPath,
            expiresAt: _now.AddDays(7));

        await runner.SweepExpiredAsync(CancellationToken.None);

        await using var ctx = NewCtx(dbName);
        var row = await ctx.TenantExportRequests.AsNoTracking().FirstAsync(r => r.Id == id);
        row.Status.Should().Be(TenantExportStatus.Completed);
        File.Exists(artifactPath).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TickAsync_WithFailingBuilder_FlipsToFailed_WithReason()
    {
        var (sp, dbName) = BuildScopeFactory();
        await SeedRequestAsync(sp, tenantId: 5, status: TenantExportStatus.Pending);
        // Force-fail by configuring an OutputPath under a file not a
        // directory — the bundle builder will throw on Directory.CreateDirectory.
        var nonDirPath = Path.Combine(_tempRoot, "notadir.txt");
        await File.WriteAllTextAsync(nonDirPath, "not a directory");
        var runner = BuildRunner(sp, outputPathOverride: nonDirPath);

        await runner.TickAsync(CancellationToken.None);

        await using var ctx = NewCtx(dbName);
        var row = await ctx.TenantExportRequests.AsNoTracking().FirstAsync();
        row.Status.Should().Be(TenantExportStatus.Failed);
        row.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TickAsync_AdoptsOrphanedRunningRows_OnExecuteAsync()
    {
        // The execute loop calls AdoptOrphanedRunningAsync once at
        // startup. Direct exercise: seed a Running row + invoke a
        // private method… instead we rely on the runner's behaviour
        // exposed via TickAsync after manually setting the row to
        // Running via the EF fixture: TickAsync alone won't pick a
        // Running row up. So we use the public adoption path that
        // ExecuteAsync calls. Simulate by spinning up the runner with
        // a tiny stoppingToken and verifying the orphan got flipped.
        var (sp, dbName) = BuildScopeFactory();
        await SeedRunningAsync(sp, tenantId: 5);
        // Add another Pending so the tick doesn't no-op the whole run.
        await SeedRequestAsync(sp, tenantId: 6, status: TenantExportStatus.Pending);

        var runner = BuildRunner(sp);
        // ExecuteAsync calls AdoptOrphanedRunningAsync as the very
        // first thing. We start it, give it a single tick, then cancel.
        using var cts = new CancellationTokenSource();
        var task = runner.StartAsync(cts.Token);
        await Task.Delay(150);
        await cts.CancelAsync();
        try { await runner.StopAsync(CancellationToken.None); } catch { /* best-effort */ }

        await using var ctx = NewCtx(dbName);
        var orphan = await ctx.TenantExportRequests.AsNoTracking().FirstAsync(r => r.TenantId == 5);
        // After adoption + processing it should be Completed (or at
        // least no longer the orphaned Running state).
        orphan.Status.Should().NotBe(TenantExportStatus.Running);
    }

    // -----------------------------------------------------------------
    // Test scaffolding
    // -----------------------------------------------------------------

    private (IServiceProvider Sp, string DbName) BuildScopeFactory()
    {
        var dbName = "tenant-export-runner-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<TenancyDbContext>(opts =>
        {
            opts.UseInMemoryDatabase(dbName);
            opts.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        });
        return (services.BuildServiceProvider(), dbName);
    }

    private TenantExportRunner BuildRunner(IServiceProvider sp,
        int maxConcurrent = 2,
        string? outputPathOverride = null)
    {
        var options = new TenantExportOptions
        {
            OutputPath = outputPathOverride ?? _tempRoot,
            RetentionDays = 7,
            MaxConcurrentExports = maxConcurrent,
            PollInterval = TimeSpan.FromMilliseconds(50),
        };
        var clock = new FakeClock(_now);
        return new TenantExportRunner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<TenantExportRunner>.Instance,
            clock);
    }

    private TenancyDbContext NewCtx(string dbName)
    {
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TenancyDbContext(options);
    }

    private async Task SeedRequestAsync(IServiceProvider sp, long tenantId, TenantExportStatus status)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.TenantExportRequests.Add(new TenantExportRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RequestedAt = _now,
            RequestedByUserId = Guid.NewGuid(),
            Format = TenantExportFormat.JsonBundle,
            Scope = TenantExportScope.All,
            Status = status,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedRunningAsync(IServiceProvider sp, long tenantId)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.TenantExportRequests.Add(new TenantExportRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RequestedAt = _now.AddSeconds(-30),
            RequestedByUserId = Guid.NewGuid(),
            Format = TenantExportFormat.JsonBundle,
            Scope = TenantExportScope.All,
            Status = TenantExportStatus.Running,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedCompletedAsync(IServiceProvider sp,
        long tenantId, string artifactPath, DateTimeOffset expiresAt)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var entity = new TenantExportRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RequestedAt = _now.AddDays(-2),
            RequestedByUserId = Guid.NewGuid(),
            Format = TenantExportFormat.JsonBundle,
            Scope = TenantExportScope.All,
            Status = TenantExportStatus.Completed,
            ArtifactPath = artifactPath,
            ArtifactSizeBytes = new FileInfo(artifactPath).Length,
            ArtifactSha256 = new byte[32],
            CompletedAt = _now.AddDays(-2),
            ExpiresAt = expiresAt,
        };
        db.TenantExportRequests.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private async Task CreateMinimalZipAsync(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("placeholder.txt");
        await using var es = entry.Open();
        await es.WriteAsync(System.Text.Encoding.UTF8.GetBytes("placeholder"));
    }

    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
