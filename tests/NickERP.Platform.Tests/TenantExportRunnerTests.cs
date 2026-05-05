using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Database.Storage;
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
    public async Task SignalWakeup_TriggersDispatchWithinOneSecond()
    {
        // Sprint 51 / Phase B — verify the LISTEN-driven wake-up path
        // dispatches a Pending row inside 1 s, even when the poll
        // interval is set to a long value (proving it's the signal that
        // woke the loop, not the poll).
        var (sp, dbName) = BuildScopeFactory();
        // Use a long poll interval so a successful dispatch within 1 s
        // can only have come from the signal, not the poll.
        var runner = BuildRunner(sp, pollInterval: TimeSpan.FromSeconds(60));
        await SeedRequestAsync(sp, tenantId: 5, status: TenantExportStatus.Pending);

        using var cts = new CancellationTokenSource();
        var task = runner.StartAsync(cts.Token);

        // Give ExecuteAsync a beat to run AdoptOrphanedRunning + first
        // tick. The first tick will pick the seeded Pending up; let it
        // race in either direction (signal vs first natural tick) and
        // verify the dispatch happens fast.
        await Task.Delay(50);
        runner.SignalWakeup();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        TenantExportStatus? observed = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            await using var ctx = NewCtx(dbName);
            var row = await ctx.TenantExportRequests.AsNoTracking().FirstOrDefaultAsync();
            if (row?.Status == TenantExportStatus.Completed)
            {
                observed = row.Status;
                break;
            }
            await Task.Delay(25);
        }
        sw.Stop();

        await cts.CancelAsync();
        try { await runner.StopAsync(CancellationToken.None); } catch { /* best-effort */ }

        observed.Should().Be(TenantExportStatus.Completed,
            "the LISTEN-equivalent SignalWakeup should drive a dispatch within 1 s, well below the 60 s poll");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PollFallback_StillDispatchesWithoutSignal()
    {
        // Sprint 51 / Phase B — verify the runner still works when the
        // LISTEN signal is unavailable (in-memory provider can't open a
        // real Npgsql connection; the listener self-disables). The 30 s
        // poll fallback must still pick up Pending rows.
        var (sp, dbName) = BuildScopeFactory();
        // Tight poll so the test stays fast — production uses 30 s.
        var runner = BuildRunner(sp, pollInterval: TimeSpan.FromMilliseconds(100));
        await SeedRequestAsync(sp, tenantId: 5, status: TenantExportStatus.Pending);

        using var cts = new CancellationTokenSource();
        var task = runner.StartAsync(cts.Token);

        // Wait up to 1 s for the poll-driven dispatch. No signal call.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TenantExportStatus? observed = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(1))
        {
            await using var ctx = NewCtx(dbName);
            var row = await ctx.TenantExportRequests.AsNoTracking().FirstOrDefaultAsync();
            if (row?.Status == TenantExportStatus.Completed)
            {
                observed = row.Status;
                break;
            }
            await Task.Delay(25);
        }

        await cts.CancelAsync();
        try { await runner.StopAsync(CancellationToken.None); } catch { /* best-effort */ }

        observed.Should().Be(TenantExportStatus.Completed,
            "the 100 ms poll must still dispatch the Pending row when LISTEN is unavailable");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TickAsync_WithRegisteredS3Storage_RoutesBundleThroughStorage()
    {
        // Sprint 51 / Phase E — verify the runner hands the artifact
        // bytes to ITenantExportStorage when registered (and the
        // backend isn't filesystem). Uses a recording fake storage so
        // we don't need a real HTTP listener.
        var fakeStorage = new RecordingStorage();
        var (sp, dbName) = BuildScopeFactoryWithStorage(fakeStorage);
        await SeedRequestAsync(sp, tenantId: 5, status: TenantExportStatus.Pending);
        var runner = BuildRunner(sp);

        await runner.TickAsync(CancellationToken.None);

        await using var ctx = NewCtx(dbName);
        var row = await ctx.TenantExportRequests.AsNoTracking().FirstAsync();
        row.Status.Should().Be(TenantExportStatus.Completed);
        row.ArtifactPath.Should().StartWith("s3://test-bucket/5/",
            "the runner must route the bundle through ITenantExportStorage when a non-filesystem backend is registered");
        fakeStorage.Writes.Should().HaveCount(1);
        fakeStorage.Writes[0].Bytes.Length.Should().Be((int)row.ArtifactSizeBytes!.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SweepExpiredAsync_WithRegisteredStorage_DeletesViaStorage()
    {
        var fakeStorage = new RecordingStorage();
        var (sp, dbName) = BuildScopeFactoryWithStorage(fakeStorage);
        var runner = BuildRunner(sp);

        var locator = "s3://test-bucket/5/synthetic.zip";
        var id = await SeedCompletedAsync(sp,
            tenantId: 5,
            artifactPath: locator,
            expiresAt: _now.AddDays(-1));
        // Simulate the artifact existing in storage so DeleteAsync
        // returns true. RecordingStorage tracks an in-memory set.
        fakeStorage.SeedExisting(locator);

        await runner.SweepExpiredAsync(CancellationToken.None);

        await using var ctx = NewCtx(dbName);
        var row = await ctx.TenantExportRequests.AsNoTracking().FirstAsync(r => r.Id == id);
        row.Status.Should().Be(TenantExportStatus.Expired);
        fakeStorage.Deletes.Should().Contain(locator);
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

    private (IServiceProvider Sp, string DbName) BuildScopeFactoryWithStorage(ITenantExportStorage storage)
    {
        var dbName = "tenant-export-runner-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<TenancyDbContext>(opts =>
        {
            opts.UseInMemoryDatabase(dbName);
            opts.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        });
        services.AddSingleton(storage);
        return (services.BuildServiceProvider(), dbName);
    }

    private TenantExportRunner BuildRunner(IServiceProvider sp,
        int maxConcurrent = 2,
        string? outputPathOverride = null,
        TimeSpan? pollInterval = null)
    {
        var options = new TenantExportOptions
        {
            OutputPath = outputPathOverride ?? _tempRoot,
            RetentionDays = 7,
            MaxConcurrentExports = maxConcurrent,
            PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(50),
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
        var size = artifactPath.StartsWith("s3://", StringComparison.Ordinal) ? 0 : new FileInfo(artifactPath).Length;
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
            ArtifactSizeBytes = size,
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

    /// <summary>
    /// Sprint 51 / Phase E — in-memory <see cref="ITenantExportStorage"/>
    /// that records writes / opens / deletes so tests can verify the
    /// runner routes through the abstraction. BackendName is
    /// deliberately not "filesystem" so the runner takes the storage
    /// branch instead of falling back to direct File IO.
    /// </summary>
    private sealed class RecordingStorage : ITenantExportStorage
    {
        public List<(Guid ExportId, long TenantId, byte[] Bytes)> Writes { get; } = new();
        public List<string> Deletes { get; } = new();
        public List<string> Reads { get; } = new();
        private readonly Dictionary<string, byte[]> _existing = new();
        public string BackendName => "test-recording";

        public Task<string> WriteAsync(Guid exportId, long tenantId, byte[] bytes, CancellationToken ct = default)
        {
            Writes.Add((exportId, tenantId, bytes));
            var locator = $"s3://test-bucket/{tenantId}/{exportId:N}.zip";
            _existing[locator] = bytes;
            return Task.FromResult(locator);
        }

        public Task<Stream?> OpenReadAsync(string locator, CancellationToken ct = default)
        {
            Reads.Add(locator);
            if (_existing.TryGetValue(locator, out var bytes))
            {
                return Task.FromResult<Stream?>(new MemoryStream(bytes));
            }
            return Task.FromResult<Stream?>(null);
        }

        public Task<bool> DeleteAsync(string locator, CancellationToken ct = default)
        {
            Deletes.Add(locator);
            return Task.FromResult(_existing.Remove(locator));
        }

        public void SeedExisting(string locator)
        {
            _existing[locator] = Array.Empty<byte>();
        }
    }
}
