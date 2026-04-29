using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;

namespace NickERP.Inspection.Imaging;

/// <summary>
/// Phase F5 — image source-store eviction.
///
/// <para>
/// The image pipeline keeps verbatim adapter output under
/// <c>{StorageRoot}/source/</c>, content-addressed by SHA-256. Once a
/// case closes (or is cancelled) we no longer need those bytes — the
/// rendered derivatives in <c>{StorageRoot}/render/</c> remain available
/// for any review or audit replay.
/// </para>
///
/// <para>
/// On a 1-hour timer (configurable via
/// <see cref="ImagingOptions.SourceJanitorIntervalMinutes"/>), this
/// worker:
/// </para>
/// <list type="number">
///   <item>Finds <see cref="ScanArtifact"/> rows whose only-referencing
///   case is <c>Closed</c> or <c>Cancelled</c> AND whose
///   <see cref="ScanArtifact.CreatedAt"/> is older than
///   <see cref="ImagingOptions.SourceRetentionDays"/>.</item>
///   <item>Deletes each candidate's source blob from disk via
///   <see cref="IImageStore"/>'s underlying file path. The
///   <see cref="ScanArtifact"/> row itself stays — only the source
///   bytes go.</item>
/// </list>
///
/// <para>
/// We don't delete the row in this version: the row carries
/// <c>StorageUri</c> + <c>ContentHash</c> + dimension metadata that the
/// audit log still references. A future "hard delete" pass can sweep
/// rows whose source has been gone for &gt; 1 year.
/// </para>
/// </summary>
public sealed class SourceJanitorWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<ImagingOptions> _opts;
    private readonly ILogger<SourceJanitorWorker> _logger;

    // Sprint 9 / FU-host-status — probe scratchpad (see BackgroundServiceProbeState).
    private readonly BackgroundServiceProbeState _probe = new();

    public SourceJanitorWorker(
        IServiceProvider services,
        IOptions<ImagingOptions> opts,
        ILogger<SourceJanitorWorker> logger)
    {
        _services = services;
        _opts = opts;
        _logger = logger;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(SourceJanitorWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _opts.Value.SourceJanitorIntervalMinutes));
        _probe.SetPollInterval(interval);
        _logger.LogInformation(
            "SourceJanitorWorker started — sweeping every {Interval}m, retention {Days}d.",
            interval.TotalMinutes, _opts.Value.SourceRetentionDays);

        // Stagger first sweep slightly so it doesn't fight the host's
        // first request burst for I/O.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
        catch (TaskCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var deleted = await SweepOnceAsync(ct);
                _probe.RecordTickSuccess();
                if (deleted > 0)
                {
                    _logger.LogInformation(
                        "SourceJanitorWorker evicted {Count} source blob(s).", deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "SourceJanitorWorker cycle failed; will retry next interval.");
            }

            try { await Task.Delay(interval, ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// Run one eviction sweep across every active tenant. Returns the
    /// total number of blobs deleted in this cycle. Public for
    /// unit-testing — callers should still go through the background
    /// service in production.
    ///
    /// <para>
    /// H1 — eviction is intrinsically per-tenant: tenant A's open case
    /// must NOT pin tenant B's blob, and tenant B's closed case must
    /// NOT cause tenant A's blob to disappear. Walk active tenants from
    /// <see cref="TenancyDbContext"/> (the only RLS-exempt table) and
    /// run the eviction logic with <c>app.tenant_id</c> pushed per
    /// iteration so RLS narrows every query to that tenant's rows.
    /// Mirrors <c>ScannerIngestionWorker.DiscoverActiveInstancesAsync</c>.
    /// </para>
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<InspectionDbContext>();
        var store = sp.GetRequiredService<IImageStore>();
        var tenancy = sp.GetRequiredService<TenancyDbContext>();
        var tenantContext = sp.GetRequiredService<ITenantContext>();

        // tenancy.tenants is intentionally NOT under RLS — root of the
        // tenancy graph — so this read works at app.tenant_id='0'.
        var activeTenantIds = await tenancy.Tenants
            .AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => t.Id)
            .ToListAsync(ct);

        if (activeTenantIds.Count == 0) return 0;

        int totalDeleted = 0;
        foreach (var tenantId in activeTenantIds)
        {
            ct.ThrowIfCancellationRequested();

            tenantContext.SetTenant(tenantId);

            // Force a fresh connection so the connection interceptor
            // re-runs SET app.tenant_id with the new value. The pooled
            // connection might otherwise still carry the previous tenant.
            try
            {
                if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                    await db.Database.CloseConnectionAsync();
            }
            catch
            {
                // Best-effort — the next query opens a fresh pooled
                // connection and the interceptor will set the tenant.
            }

            totalDeleted += await SweepTenantAsync(db, store, ct);
        }

        return totalDeleted;
    }

    /// <summary>
    /// Single-tenant sweep. Caller is responsible for setting the
    /// tenant on <see cref="ITenantContext"/> before invoking; RLS
    /// narrows every query to that tenant.
    /// </summary>
    private async Task<int> SweepTenantAsync(
        InspectionDbContext db,
        IImageStore store,
        CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, _opts.Value.SourceRetentionDays));

        // Find ScanArtifacts whose case is Closed or Cancelled (see
        // InspectionWorkflowState) and whose creation is older than the
        // retention cutoff. Project only the fields IImageStore needs to
        // compute the source path. Per ARCHITECTURE §7.7 the source
        // filename is {hash[0..2]}/{hash}.{ext} — we don't need the
        // actual storage URI, only the hash + mime type, which
        // DiskImageStore turns into a path.
        var candidates = await (
            from a in db.ScanArtifacts.AsNoTracking()
            join s in db.Scans.AsNoTracking() on a.ScanId equals s.Id
            join c in db.Cases.AsNoTracking() on s.CaseId equals c.Id
            where (c.State == InspectionWorkflowState.Closed
                   || c.State == InspectionWorkflowState.Cancelled)
                  && a.CreatedAt < cutoff
            select new { a.ContentHash, a.MimeType }
        ).Distinct().ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        // Reduce to unique hashes — multiple ScanArtifact rows can
        // share a source blob (content-addressed).
        var byHash = candidates
            .GroupBy(x => x.ContentHash)
            .Select(g => new { ContentHash = g.Key, MimeType = g.First().MimeType })
            .ToList();

        // Load the set of hashes still referenced by NON-evictable
        // cases — we must NOT delete a blob another open case shares.
        var hashes = byHash.Select(x => x.ContentHash).ToList();
        var stillReferenced = await (
            from a in db.ScanArtifacts.AsNoTracking()
            join s in db.Scans.AsNoTracking() on a.ScanId equals s.Id
            join c in db.Cases.AsNoTracking() on s.CaseId equals c.Id
            where hashes.Contains(a.ContentHash)
                  && c.State != InspectionWorkflowState.Closed
                  && c.State != InspectionWorkflowState.Cancelled
            select a.ContentHash
        ).Distinct().ToListAsync(ct);

        var stillReferencedSet = new HashSet<string>(stillReferenced, StringComparer.OrdinalIgnoreCase);

        int deleted = 0;
        foreach (var h in byHash)
        {
            ct.ThrowIfCancellationRequested();
            if (stillReferencedSet.Contains(h.ContentHash))
            {
                continue;
            }
            try
            {
                if (store is DiskImageStore disk)
                {
                    var path = disk.GetSourcePath(h.ContentHash, MimeToExtension(h.MimeType));
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                else
                {
                    // Future stores (S3, Azure Blob, etc.) will need
                    // their own delete API; logging keeps the operator
                    // aware that nothing was evicted.
                    _logger.LogDebug(
                        "IImageStore {Type} does not support source eviction; skipping {Hash}.",
                        store.GetType().Name, h.ContentHash);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to evict source blob for hash {Hash}; will retry next sweep.",
                    h.ContentHash);
            }
        }

        return deleted;
    }

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
