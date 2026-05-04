using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tenancy.Database.Workers;

/// <summary>
/// Sprint 25 — picks Pending <see cref="TenantExportRequest"/> rows out
/// of the queue, builds the bundle on disk, and transitions the row to
/// Completed / Failed. Also sweeps Completed rows past their
/// <see cref="TenantExportRequest.ExpiresAt"/> and flips them to
/// Expired (with the artifact deleted from disk).
/// </summary>
/// <remarks>
/// <para>
/// Polls every <see cref="TenantExportOptions.PollInterval"/> (default
/// 30 s). Concurrency capped at
/// <see cref="TenantExportOptions.MaxConcurrentExports"/> (default 2)
/// so a queue of large exports doesn't saturate the platform DBs.
/// </para>
/// <para>
/// Crash-recovery: the runner adopts any Running row whose row was
/// orphaned by a previous host crash on startup — it re-flips them to
/// Pending so they get picked up again. Idempotency comes from the
/// build's atomic temp-file rename (see <see cref="TenantExportBundleBuilder.BuildAsync"/>).
/// </para>
/// <para>
/// LISTEN/NOTIFY pickup is a future enhancement — today's poll is fine
/// for ops volumes (a few exports per day).
/// </para>
/// </remarks>
public sealed class TenantExportRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TenantExportOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TenantExportRunner> _logger;

    public TenantExportRunner(
        IServiceScopeFactory scopeFactory,
        TenantExportOptions options,
        ILogger<TenantExportRunner> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TenantExportRunner starting — pollInterval={Poll}, maxConcurrent={Max}, outputPath={Path}.",
            _options.PollInterval, _options.MaxConcurrentExports, _options.OutputPath);

        // Adopt orphaned Running rows once at startup. A previous host
        // that crashed mid-export leaves them dangling; flipping them
        // back to Pending lets us pick them up again. The bundle builder
        // is idempotent (writes to a temp file then moves into place).
        try
        {
            await AdoptOrphanedRunningAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TenantExportRunner orphan-adoption failed; continuing.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TenantExportRunner tick failed; will retry next poll.");
            }
            try
            {
                await Task.Delay(_options.PollInterval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("TenantExportRunner stopping.");
    }

    /// <summary>
    /// Run one poll cycle: sweep expired rows, then pick up to
    /// MaxConcurrentExports Pending rows and process them in parallel.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await SweepExpiredAsync(ct);

        var picked = await PickPendingAsync(_options.MaxConcurrentExports, ct);
        if (picked.Count == 0) return;

        var tasks = picked.Select(id => ProcessOneAsync(id, ct)).ToList();
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Atomically claim up to <paramref name="batchSize"/> Pending rows
    /// by flipping their status to Running. Returns the ids picked.
    /// </summary>
    private async Task<List<Guid>> PickPendingAsync(int batchSize, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        // FOR UPDATE SKIP LOCKED would be the multi-host story; today we
        // assume single-host. The status flip in a transaction prevents
        // cross-tick double-pickup within a host.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = await db.TenantExportRequests
            .Where(r => r.Status == TenantExportStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .Take(batchSize)
            .ToListAsync(ct);
        foreach (var p in pending)
        {
            p.Status = TenantExportStatus.Running;
        }
        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return pending.Select(p => p.Id).ToList();
    }

    private async Task ProcessOneAsync(Guid exportId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var row = await db.TenantExportRequests.FirstOrDefaultAsync(r => r.Id == exportId, ct);
        if (row is null)
        {
            _logger.LogWarning("TenantExportRunner — picked row {ExportId} disappeared mid-process.", exportId);
            return;
        }

        try
        {
            var artifactPath = ResolveArtifactPath(row.TenantId, row.Id);
            _logger.LogInformation(
                "TenantExportRunner building export {ExportId} for tenant {TenantId}: format={Format}, scope={Scope}, target={Path}.",
                row.Id, row.TenantId, row.Format, row.Scope, artifactPath);

            var (size, sha) = await TenantExportBundleBuilder.BuildAsync(
                outputPath: artifactPath,
                tenantId: row.TenantId,
                request: row,
                options: _options,
                logger: _logger,
                ct: ct);

            var now = _clock.GetUtcNow();
            row.Status = TenantExportStatus.Completed;
            row.ArtifactPath = artifactPath;
            row.ArtifactSizeBytes = size;
            row.ArtifactSha256 = sha;
            row.CompletedAt = now;
            row.ExpiresAt = now.AddDays(_options.RetentionDays);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "TenantExportRunner completed export {ExportId} ({Size} bytes); expires {ExpiresAt}.",
                row.Id, size, row.ExpiresAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "TenantExportRunner FAILED export {ExportId} for tenant {TenantId}.",
                row.Id, row.TenantId);
            row.Status = TenantExportStatus.Failed;
            row.CompletedAt = _clock.GetUtcNow();
            var msg = $"{ex.GetType().Name}: {ex.Message}";
            row.FailureReason = msg.Length <= 1000 ? msg : msg[..997] + "...";
            try { await db.SaveChangesAsync(ct); }
            catch { /* best-effort — failure-state save is informational */ }
        }
    }

    /// <summary>
    /// Flip Completed rows past their ExpiresAt to Expired and delete
    /// the artifact on disk. Idempotent — re-running the sweep against
    /// already-Expired rows is a no-op.
    /// </summary>
    public async Task SweepExpiredAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var now = _clock.GetUtcNow();
        var expired = await db.TenantExportRequests
            .Where(r => r.Status == TenantExportStatus.Completed
                && r.ExpiresAt != null
                && r.ExpiresAt <= now)
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        foreach (var row in expired)
        {
            if (!string.IsNullOrWhiteSpace(row.ArtifactPath) && File.Exists(row.ArtifactPath))
            {
                try
                {
                    File.Delete(row.ArtifactPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "TenantExportRunner sweep — failed to delete artifact {Path} for export {ExportId}; will leave row in Completed for retry.",
                        row.ArtifactPath, row.Id);
                    continue;
                }
            }
            row.Status = TenantExportStatus.Expired;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "TenantExportRunner sweep — expired {Count} export(s).",
            expired.Count(r => r.Status == TenantExportStatus.Expired));
    }

    private async Task AdoptOrphanedRunningAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var orphans = await db.TenantExportRequests
            .Where(r => r.Status == TenantExportStatus.Running)
            .ToListAsync(ct);
        if (orphans.Count == 0) return;
        foreach (var row in orphans)
        {
            row.Status = TenantExportStatus.Pending;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "TenantExportRunner adopted {Count} orphaned Running row(s); flipped back to Pending.",
            orphans.Count);
    }

    private string ResolveArtifactPath(long tenantId, Guid exportId)
    {
        var root = string.IsNullOrWhiteSpace(_options.OutputPath)
            ? Path.Combine(AppContext.BaseDirectory, "var", "tenant-exports")
            : Path.IsPathRooted(_options.OutputPath)
                ? _options.OutputPath
                : Path.Combine(AppContext.BaseDirectory, _options.OutputPath);
        var dir = Path.Combine(root, tenantId.ToString());
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{exportId:N}.zip");
    }
}
