using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Entities;
using Npgsql;

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
/// Sprint 51 / Phase B — LISTEN/NOTIFY pickup. The runner subscribes
/// to <see cref="NotifyChannel"/> on startup and triggers an
/// out-of-band tick when <see cref="TenantExportService.RequestExportAsync"/>
/// emits a NOTIFY. The 30 s poll stays as the fallback for the case
/// where the LISTEN connection is unavailable (in-memory provider in
/// tests, network blip, listener restart).
/// </para>
/// </remarks>
public sealed class TenantExportRunner : BackgroundService
{
    /// <summary>
    /// Sprint 51 / Phase B — Postgres NOTIFY channel name. Postgres
    /// channel identifiers are case-sensitive but max 63 characters and
    /// dot/dash-free; we use a short ASCII slug so every host agrees on
    /// the same name.
    /// </summary>
    public const string NotifyChannel = "nickerp_tenant_export_requested";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TenantExportOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TenantExportRunner> _logger;
    /// <summary>Wakes the poll loop when a NOTIFY comes in. The
    /// fallback Task.Delay races against this on each tick so the
    /// cycle dispatches new requests within ~1 s without re-engineering
    /// the loop.</summary>
    private readonly SemaphoreSlim _notifySignal = new(0, int.MaxValue);

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

        // Sprint 51 / Phase B — start the LISTEN listener as a parallel
        // background task. It subscribes to the dedicated channel and
        // releases the wake-up semaphore whenever a NOTIFY arrives. If
        // the connection drops, the listener loops with a back-off and
        // re-subscribes — meanwhile the poll fallback below keeps
        // dispatching at the configured interval.
        var listenTask = Task.Run(() => RunListenLoopAsync(stoppingToken), stoppingToken);

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
                // Wait for either the poll interval OR a NOTIFY-driven
                // wake-up. WaitAsync(timeout) returns true on signal,
                // false on timeout — both flow into the next tick.
                await _notifySignal.WaitAsync(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("TenantExportRunner stopping.");
        try { await listenTask; } catch { /* listener best-effort on shutdown */ }
    }

    /// <summary>
    /// Sprint 51 / Phase B — long-running LISTEN loop. Resolves the
    /// platform connection string (same env var the bundle builder uses
    /// for cross-DB reads), opens a dedicated NpgsqlConnection,
    /// subscribes to <see cref="NotifyChannel"/>, and waits for
    /// notifications. Each notification releases the
    /// <see cref="_notifySignal"/> semaphore, waking the poll loop for
    /// an immediate tick. Crashes back-off via a 5 s sleep before
    /// re-trying — the poll fallback covers the gap.
    /// </summary>
    private async Task RunListenLoopAsync(CancellationToken ct)
    {
        var conn = ResolveListenConnectionString();
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogInformation(
                "TenantExportRunner LISTEN disabled — no platform connection string configured. Falling back to {Poll} poll.",
                _options.PollInterval);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var listenConn = new NpgsqlConnection(conn);
                await listenConn.OpenAsync(ct);
                listenConn.Notification += OnPostgresNotification;

                await using (var cmd = listenConn.CreateCommand())
                {
                    cmd.CommandText = $"LISTEN {NotifyChannel};";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                _logger.LogInformation(
                    "TenantExportRunner LISTEN active on channel {Channel}.", NotifyChannel);

                // WaitAsync blocks until a notification arrives or the
                // connection drops. Returns when the conn yields a
                // notification; we loop to keep waiting.
                while (!ct.IsCancellationRequested)
                {
                    await listenConn.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TenantExportRunner LISTEN loop dropped; retrying in 5 s. Poll fallback covers the gap.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), _clock, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }
    }

    private void OnPostgresNotification(object _, NpgsqlNotificationEventArgs e)
    {
        if (!string.Equals(e.Channel, NotifyChannel, StringComparison.Ordinal)) return;
        SignalWakeup();
    }

    /// <summary>
    /// Sprint 51 / Phase B — exposed for tests so we can drive the
    /// LISTEN-style wake-up path without a live Postgres listener. The
    /// production runner only calls this internally from the LISTEN
    /// callback. Cap the signal at 1 — a burst of NOTIFY events still
    /// dispatches in batches of <c>MaxConcurrentExports</c> per tick.
    /// </summary>
    internal void SignalWakeup()
    {
        try
        {
            if (_notifySignal.CurrentCount == 0)
            {
                _notifySignal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already at the cap; nothing to do.
        }
    }

    private string? ResolveListenConnectionString()
    {
        // Prefer the explicit options string; fall back to the env var
        // that AddNickErpTenantExport seeds. Tests can leave both null
        // (in-memory provider can't NOTIFY anyway) and the listener
        // self-disables.
        if (!string.IsNullOrWhiteSpace(_options.PlatformConnectionString))
        {
            return _options.PlatformConnectionString;
        }
        return Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION");
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
