using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;

namespace NickERP.Inspection.Imaging;

/// <summary>
/// Background worker that finds <see cref="ScanArtifact"/> rows missing one
/// or both of their derivatives (thumbnail / preview), reads the source
/// bytes from <see cref="IImageStore"/>, runs <see cref="IImageRenderer"/>,
/// and persists the result.
///
/// Skeleton implementation — polls the source table on a fixed interval.
/// The architecture spec calls for a SQL-backed durable queue + an
/// in-memory <c>Channel&lt;long&gt;</c> for fast wake-up; both move in once
/// we know the polling tax matters in practice. Today this is fine: the
/// query is two indexed left joins and the cardinality is bounded by the
/// scan rate.
///
/// Idempotency — for each <see cref="ScanArtifact"/> we look at which
/// <see cref="ScanRenderArtifact"/> rows exist, render only the missing
/// ones, and use UPSERT-on-(ScanArtifactId, Kind) so re-runs are silent.
/// Errors are logged and the artifact gets retried on the next cycle;
/// no permanent failure marker yet (good first follow-up).
/// </summary>
public sealed class PreRenderWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<ImagingOptions> _opts;
    private readonly ILogger<PreRenderWorker> _logger;

    public PreRenderWorker(
        IServiceProvider services,
        IOptions<ImagingOptions> opts,
        ILogger<PreRenderWorker> logger)
    {
        _services = services;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(1, _opts.Value.WorkerPollIntervalSeconds));
        _logger.LogInformation("PreRenderWorker started — polling every {Interval}s, batch {Batch}",
            poll.TotalSeconds, _opts.Value.WorkerBatchSize);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var processed = await DrainOnceAsync(ct);
                if (processed > 0)
                    _logger.LogDebug("PreRenderWorker rendered {Count} derivative(s) this cycle", processed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "PreRenderWorker cycle failed; backing off");
            }

            try { await Task.Delay(poll, ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// Scan for unrendered artifacts and render up to <c>WorkerBatchSize</c>
    /// of them. Returns the number of derivative rows produced.
    /// </summary>
    private async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        // New scope per cycle so the DbContext doesn't grow unbounded
        // change-tracking state across batches.
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var renderer = scope.ServiceProvider.GetRequiredService<IImageRenderer>();
        var store = scope.ServiceProvider.GetRequiredService<IImageStore>();

        var batchSize = Math.Max(1, _opts.Value.WorkerBatchSize);

        // Pull a batch of artifacts that are missing at least one derivative.
        // We materialize the existing kinds per artifact so the worker only
        // produces what's missing. Phase F5 — also gather the set of kinds
        // that have been permanently failed so we don't keep retrying poison
        // messages.
        var batch = await db.ScanArtifacts.AsNoTracking()
            .Select(a => new
            {
                Artifact = a,
                ExistingKinds = db.ScanRenderArtifacts
                    .Where(r => r.ScanArtifactId == a.Id)
                    .Select(r => r.Kind)
                    .ToList(),
                PermanentlyFailedKinds = db.ScanRenderAttempts
                    .Where(t => t.ScanArtifactId == a.Id && t.PermanentlyFailedAt != null)
                    .Select(t => t.Kind)
                    .ToList()
            })
            .Where(x => (x.ExistingKinds.Count + x.PermanentlyFailedKinds.Count) < 2) // 2 = thumb + preview
            .OrderBy(x => x.Artifact.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        int produced = 0;
        foreach (var entry in batch)
        {
            ct.ThrowIfCancellationRequested();

            var artifact = entry.Artifact;
            var have = new HashSet<string>(entry.ExistingKinds, StringComparer.OrdinalIgnoreCase);
            var dead = new HashSet<string>(entry.PermanentlyFailedKinds, StringComparer.OrdinalIgnoreCase);

            byte[] sourceBytes;
            try
            {
                sourceBytes = await store.ReadSourceAsync(
                    artifact.ContentHash,
                    MimeToExtension(artifact.MimeType),
                    ct);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogWarning(ex,
                    "Source bytes missing for ScanArtifact {Id} (hash {Hash}). Skipping; analyst will see no preview until ingestion is replayed.",
                    artifact.Id, artifact.ContentHash);
                // Treat missing source as a render failure for both kinds —
                // otherwise we'd hammer the disk on every cycle for an
                // artifact whose source will never arrive.
                if (!have.Contains(RenderKinds.Thumbnail) && !dead.Contains(RenderKinds.Thumbnail))
                    await RecordAttemptFailureAsync(db, artifact, RenderKinds.Thumbnail, $"source bytes missing: {ex.Message}", ct);
                if (!have.Contains(RenderKinds.Preview) && !dead.Contains(RenderKinds.Preview))
                    await RecordAttemptFailureAsync(db, artifact, RenderKinds.Preview, $"source bytes missing: {ex.Message}", ct);
                continue;
            }

            if (!have.Contains(RenderKinds.Thumbnail) && !dead.Contains(RenderKinds.Thumbnail))
            {
                if (await TryRenderAndPersistAsync(db, store, renderer, artifact, RenderKinds.Thumbnail, sourceBytes, ct))
                    produced++;
            }
            if (!have.Contains(RenderKinds.Preview) && !dead.Contains(RenderKinds.Preview))
            {
                if (await TryRenderAndPersistAsync(db, store, renderer, artifact, RenderKinds.Preview, sourceBytes, ct))
                    produced++;
            }
        }

        return produced;
    }

    private async Task<bool> TryRenderAndPersistAsync(
        InspectionDbContext db,
        IImageStore store,
        IImageRenderer renderer,
        ScanArtifact artifact,
        string kind,
        byte[] sourceBytes,
        CancellationToken ct)
    {
        try
        {
            var rendered = string.Equals(kind, RenderKinds.Thumbnail, StringComparison.OrdinalIgnoreCase)
                ? await renderer.RenderThumbnailAsync(sourceBytes, ct)
                : await renderer.RenderPreviewAsync(sourceBytes, ct);

            var storageUri = await store.SaveRenderAsync(artifact.Id, kind, rendered.Bytes, ct);
            var contentHash = Convert.ToHexString(SHA256.HashData(rendered.Bytes));

            db.ScanRenderArtifacts.Add(new ScanRenderArtifact
            {
                ScanArtifactId = artifact.Id,
                Kind = kind,
                StorageUri = storageUri,
                WidthPx = rendered.WidthPx,
                HeightPx = rendered.HeightPx,
                MimeType = rendered.MimeType,
                ContentHash = contentHash,
                RenderedAt = DateTimeOffset.UtcNow,
                TenantId = artifact.TenantId
            });
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another worker (or a re-render race) beat us to it. Not an
            // error — both copies render the same content given the same
            // source bytes, so silently move on.
            _logger.LogDebug(ex,
                "Render row for {Id}/{Kind} already exists; skipping (lost a benign race).",
                artifact.Id, kind);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Phase F5 — track attempt count + last error so a poison
            // message stops being retried after MaxRenderAttempts.
            await RecordAttemptFailureAsync(db, artifact, kind, ex.Message, ct);
            _logger.LogWarning(ex,
                "Failed to render {Kind} for ScanArtifact {Id}; recorded attempt.",
                kind, artifact.Id);
            return false;
        }
    }

    /// <summary>
    /// Phase F5 — upsert a row in <c>scan_render_attempts</c> for this
    /// (artifact, kind) pair, increment the attempt count, and stamp
    /// <c>PermanentlyFailedAt</c> when it crosses
    /// <c>ImagingOptions.MaxRenderAttempts</c>.
    /// </summary>
    private async Task RecordAttemptFailureAsync(
        InspectionDbContext db,
        ScanArtifact artifact,
        string kind,
        string errorMessage,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _opts.Value.MaxRenderAttempts);

        // EF doesn't have a tidy native UPSERT; do a find-then-add/update.
        // Race-safe enough for the worker's single-host deployment model;
        // a future multi-host rollout can move this to a Postgres ON
        // CONFLICT statement (out of scope for F5).
        var existing = await db.ScanRenderAttempts
            .FirstOrDefaultAsync(t => t.ScanArtifactId == artifact.Id && t.Kind == kind, ct);

        var truncatedError = errorMessage.Length > 1900 ? errorMessage[..1900] : errorMessage;

        if (existing is null)
        {
            existing = new ScanRenderAttempt
            {
                ScanArtifactId = artifact.Id,
                Kind = kind,
                AttemptCount = 1,
                LastError = truncatedError,
                LastAttemptAt = DateTimeOffset.UtcNow,
                TenantId = artifact.TenantId
            };
            db.ScanRenderAttempts.Add(existing);
        }
        else
        {
            // Skip if already retired. Defensive — the drain query
            // already filters these out.
            if (existing.PermanentlyFailedAt is not null) return;

            existing.AttemptCount += 1;
            existing.LastError = truncatedError;
            existing.LastAttemptAt = DateTimeOffset.UtcNow;
        }

        if (existing.AttemptCount >= maxAttempts)
        {
            existing.PermanentlyFailedAt = DateTimeOffset.UtcNow;
            _logger.LogError(
                "ScanArtifact {Id} kind {Kind} permanently failed after {Attempts} attempts. Last error: {Error}",
                artifact.Id, kind, existing.AttemptCount, truncatedError);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A second worker raced us to the same row. Not fatal —
            // both increments would say the same thing; the UNIQUE
            // index on (ScanArtifactId, Kind) means at most one row
            // wins, the other retries on the next sweep.
            _logger.LogDebug(ex,
                "Lost benign race recording attempt for {Id}/{Kind}; another worker won.",
                artifact.Id, kind);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name.Contains("PostgresException", StringComparison.Ordinal) == true
           && ex.InnerException.Message.Contains("ux_render_artifact_kind", StringComparison.Ordinal);

    private static string MimeToExtension(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/tiff" => ".tiff",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".bin"
    };
}
