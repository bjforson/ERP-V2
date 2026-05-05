using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Platform.Plugins;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using System.Text.Json;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 24 / B3.1 — Cursor-based scanner sync worker.
/// Periodically pulls new scan records from every active
/// <see cref="ScannerDeviceInstance"/> whose plugin implements
/// <see cref="IScannerCursorSyncAdapter"/>. Replaces v1's
/// <c>AseBackgroundService</c>, vendor-neutralised.
///
/// <para>
/// <b>Why a separate worker (vs reusing <see cref="ScannerIngestionWorker"/>).</b>
/// The streaming worker drives one long-running per-instance task per
/// device; that's the right shape for filesystem-watcher adapters
/// (FS6000) where new scans arrive continuously. Cursor-based sources
/// (ASE SQL Server, future cloud APIs) batch — pulling once per few
/// minutes is cheaper than holding a long connection open + correctly
/// handles cursor advance + restart semantics. Different shape, different
/// worker.
/// </para>
///
/// <para>
/// <b>Cursor state.</b> Held in-memory in
/// <see cref="_cursorByInstance"/>. On host restart, the cursor resets
/// to <see cref="string.Empty"/> and the adapter re-emits every record;
/// the host's <c>Scan.IdempotencyKey</c> uniqueness
/// (<c>ux_scans_tenant_idempotency</c>) silently dedupes. Per Sprint 24
/// architectural decision: no new tracking tables for cursor state —
/// idempotency through existing unique indexes only.
/// </para>
///
/// <para>
/// <b>Default-disabled.</b>
/// <see cref="AseSyncOptions.Enabled"/> = <c>false</c> by default; ops
/// opts in per environment. A fresh deploy without an ASE adapter
/// configured no-ops cleanly.
/// </para>
///
/// <para>
/// Mirrors <see cref="ScannerIngestionWorker"/> + <see cref="OutcomePullWorker"/>:
/// cross-tenant discovery via <see cref="TenancyDbContext"/>; per-iteration
/// scope; plugin singleton + scoped DbContext.
/// </para>
/// </summary>
public sealed class AseSyncWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<AseSyncOptions> _options;
    private readonly ILogger<AseSyncWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    /// <summary>
    /// Per-instance cursor state — keyed on
    /// <see cref="ScannerDeviceInstance.Id"/>. <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// is overkill for the single-host model (cycles run sequentially)
    /// but cheap and future-proofs the worker if it ever runs on more
    /// than one thread.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _cursorByInstance = new();

    public AseSyncWorker(
        IServiceProvider services,
        IOptions<AseSyncOptions> options,
        ILogger<AseSyncWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(AseSyncWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "AseSyncWorker disabled via {Section}:Enabled=false; not starting.",
                AseSyncOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "AseSyncWorker starting — pulling every {Interval}, batch limit {BatchLimit}, max records/cycle {MaxRecords}.",
            opts.PollInterval, opts.BatchLimit, opts.MaxRecordsPerCycle);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var pulled = await PullOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (pulled > 0)
                {
                    _logger.LogInformation(
                        "AseSyncWorker cycle pulled {Count} record(s) total.", pulled);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "AseSyncWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One cycle: walk every active tenant, find every active scanner
    /// whose plugin implements <see cref="IScannerCursorSyncAdapter"/>,
    /// pull a batch, write the resulting <see cref="Scan"/> rows.
    /// Returns the count of records pulled across all instances.
    /// </summary>
    /// <remarks>
    /// Internal so the test project can drive a single cycle without
    /// the full hosted-service start dance.
    /// </remarks>
    internal async Task<int> PullOnceAsync(CancellationToken ct)
    {
        var devices = await DiscoverActiveCursorDevicesAsync(ct);
        if (devices.Count == 0) return 0;

        var totalPulled = 0;
        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                totalPulled += await PullOneAsync(device, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AseSyncInstruments.PullFailedTotal.Add(1,
                    new KeyValuePair<string, object?>("type_code", device.TypeCode));
                _logger.LogError(ex,
                    "AseSyncWorker pull failed for tenant={TenantId} instance={InstanceId} type={TypeCode}; cursor unchanged.",
                    device.TenantId, device.InstanceId, device.TypeCode);
            }
        }

        return totalPulled;
    }

    private async Task<IReadOnlyList<DeviceDescriptor>> DiscoverActiveCursorDevicesAsync(CancellationToken ct)
    {
        var results = new List<DeviceDescriptor>();

        using var discoveryScope = _services.CreateScope();
        var sp = discoveryScope.ServiceProvider;
        var tenantsDb = sp.GetRequiredService<TenancyDbContext>();
        var tenant = sp.GetRequiredService<ITenantContext>();
        var inspectionDb = sp.GetRequiredService<InspectionDbContext>();
        var plugins = sp.GetRequiredService<IPluginRegistry>();

        var activeTenantIds = await tenantsDb.Tenants
            .AsNoTracking()
            .Where(t => t.State == TenantState.Active)
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var tenantId in activeTenantIds)
        {
            ct.ThrowIfCancellationRequested();
            tenant.SetTenant(tenantId);

            try
            {
                if (inspectionDb.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                    await inspectionDb.Database.CloseConnectionAsync();
            }
            catch { /* best-effort */ }

            var devices = await inspectionDb.ScannerDeviceInstances
                .AsNoTracking()
                .Where(d => d.IsActive)
                .Select(d => new DeviceDescriptor(
                    tenantId,
                    d.Id,
                    d.LocationId,
                    d.StationId,
                    d.TypeCode,
                    d.ConfigJson))
                .ToListAsync(ct);

            // Filter to devices whose plugin implements the cursor-sync
            // contract. Resolution exceptions (no plugin / wrong cap)
            // are silenced — they're "not a cursor adapter", not failures.
            foreach (var device in devices)
            {
                if (PluginImplementsCursorSync(plugins, sp, device.TypeCode))
                {
                    results.Add(device);
                }
            }
        }

        return results;
    }

    private static bool PluginImplementsCursorSync(IPluginRegistry plugins, IServiceProvider sp, string typeCode)
    {
        try
        {
            // Resolution to IScannerAdapter is cheap; downcast is the
            // capability gate. We can't directly resolve to
            // IScannerCursorSyncAdapter because the plugin metadata
            // pivots off the IScannerAdapter contract.
            var adapter = plugins.Resolve<IScannerAdapter>("inspection", typeCode, sp);
            return adapter is IScannerCursorSyncAdapter;
        }
        catch
        {
            return false;
        }
    }

    private async Task<int> PullOneAsync(DeviceDescriptor device, CancellationToken ct)
    {
        var opts = _options.Value;

        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var plugins = sp.GetRequiredService<IPluginRegistry>();
        var db = sp.GetRequiredService<InspectionDbContext>();
        var imageStore = sp.GetRequiredService<NickERP.Inspection.Imaging.IImageStore>();
        tenant.SetTenant(device.TenantId);

        IScannerAdapter generic;
        try { generic = plugins.Resolve<IScannerAdapter>("inspection", device.TypeCode, sp); }
        catch (KeyNotFoundException) { return 0; }
        catch (InvalidOperationException) { return 0; }

        if (generic is not IScannerCursorSyncAdapter cursor)
        {
            return 0; // discovery filter should have caught this; defensive
        }

        var cfg = new ScannerDeviceConfig(
            DeviceId: device.InstanceId,
            LocationId: device.LocationId,
            StationId: device.StationId,
            TenantId: device.TenantId,
            ConfigJson: device.ConfigJson);

        var startCursor = _cursorByInstance.GetValueOrDefault(device.InstanceId, string.Empty);
        var pulledThisDevice = 0;
        var currentCursor = startCursor;

        // Drain quota — keep pulling within one cycle while HasMore is
        // true, but stop at MaxRecordsPerCycle so other workers + tenants
        // get a turn.
        while (pulledThisDevice < opts.MaxRecordsPerCycle && !ct.IsCancellationRequested)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CursorSyncBatch batch;
            try
            {
                batch = await cursor.PullAsync(cfg, currentCursor, opts.BatchLimit, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                AseSyncInstruments.PullFailedTotal.Add(1,
                    new KeyValuePair<string, object?>("type_code", device.TypeCode));
                throw;
            }
            sw.Stop();
            AseSyncInstruments.PullLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("type_code", device.TypeCode));

            if (batch.Records.Count == 0)
            {
                // Even an empty batch may advance the cursor (the source
                // is past the last successful pull point). Persist the
                // adapter's NextCursor regardless.
                _cursorByInstance[device.InstanceId] = batch.NextCursor;
                break;
            }

            await PersistRecordsAsync(db, imageStore, generic, device, batch.Records, ct);
            pulledThisDevice += batch.Records.Count;
            currentCursor = batch.NextCursor;
            _cursorByInstance[device.InstanceId] = currentCursor;

            AseSyncInstruments.RecordsPulledTotal.Add(batch.Records.Count,
                new KeyValuePair<string, object?>("type_code", device.TypeCode));

            if (!batch.HasMore) break;
        }

        return pulledThisDevice;
    }

    /// <summary>
    /// Persist a batch of <see cref="CursorSyncRecord"/> as
    /// <see cref="Scan"/> + <see cref="ScanArtifact"/> rows. Each record
    /// goes through three steps:
    /// <list type="number">
    ///   <item><description>Adapter <c>ParseScanAsync</c> — vendor bytes
    ///   → canonical <see cref="NickERP.Inspection.Edge.Abstractions.ScanPackage"/>
    ///   bundle. Stays in-process; the host doesn't seal in this path
    ///   (the package is for ingestion, not edge-replay).</description></item>
    ///   <item><description>Case keying — reuse an open
    ///   <see cref="InspectionCase"/> with the same
    ///   <c>(LocationId, SubjectIdentifier=record.SourceReference)</c>
    ///   opened in the last 24h, or open a fresh one. Same shape as
    ///   FS6000's IngestRawArtifactAsync path, so the case-reuse
    ///   semantics across vendors stay aligned.</description></item>
    ///   <item><description>Idempotency — Scan rows carry the
    ///   adapter's <see cref="CursorSyncRecord.IdempotencyKey"/>; the
    ///   unique index <c>ux_scans_tenant_idempotency</c> on
    ///   (TenantId, IdempotencyKey) dedupes replays at the DB layer.
    ///   We pre-check before insert so the common case avoids the
    ///   exception-throw round-trip.</description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Per-record SaveChanges.</b> Each record is its own transaction
    /// — a poison record in the middle of a batch doesn't take down the
    /// whole cursor advance. The Scan + ScanArtifact pair commits
    /// together (foreign key from artifact.ScanId).
    /// </para>
    ///
    /// <para>
    /// <b>Storage path.</b> The cursor record's bytes go through
    /// <see cref="IImageStore.SaveSourceAsync"/> the same way FS6000's
    /// triplets do — so the pre-render worker, image-route handler, and
    /// retention enforcer all reach back to the bytes through the same
    /// content-addressable path.
    /// </para>
    /// </summary>
    private async Task PersistRecordsAsync(
        InspectionDbContext db,
        NickERP.Inspection.Imaging.IImageStore imageStore,
        IScannerAdapter adapter,
        DeviceDescriptor device,
        IReadOnlyList<CursorSyncRecord> records,
        CancellationToken ct)
    {
        // ScannerCapabilities for the canonical ParseScanAsync path —
        // pulled directly from the adapter; the worker doesn't override.
        var capabilities = adapter.Capabilities;

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PersistOneRecordAsync(db, adapter, imageStore, capabilities, device, record, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AseSyncInstruments.PersistFailedTotal.Add(1,
                    new KeyValuePair<string, object?>("type_code", device.TypeCode));
                _logger.LogWarning(ex,
                    "AseSyncWorker persist failed for tenant={TenantId} instance={InstanceId} source-ref={SourceReference}; continuing batch.",
                    device.TenantId, device.InstanceId, record.SourceReference);
            }
        }
    }

    /// <summary>
    /// Persist one cursor record. Idempotent: a duplicate
    /// <see cref="CursorSyncRecord.IdempotencyKey"/> short-circuits via
    /// the pre-check + the unique-index race fallback.
    /// </summary>
    private async Task PersistOneRecordAsync(
        InspectionDbContext db,
        IScannerAdapter adapter,
        NickERP.Inspection.Imaging.IImageStore imageStore,
        ScannerCapabilities capabilities,
        DeviceDescriptor device,
        CursorSyncRecord record,
        CancellationToken ct)
    {
        // 1. Pre-check idempotency — common-path fast skip.
        var existingScan = await db.Scans
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.IdempotencyKey == record.IdempotencyKey, ct);
        if (existingScan is not null)
        {
            AseSyncInstruments.DedupedTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", device.TypeCode));
            return;
        }

        // 2. Run the adapter's canonical ParseScanAsync — the package +
        //    artifact projection.
        var parsed = await adapter.ParseScanAsync(record.Bytes, capabilities, ct);
        var primary = parsed.Artifacts.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Adapter '{device.TypeCode}' returned a ParsedScan with no Artifacts; cannot persist.");

        // 3. Resolve / open the parent case keyed on
        //    (LocationId, SubjectIdentifier=record.SourceReference).
        //    Mirrors the FS6000 IngestRawArtifactAsync 24h-reuse window.
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var subjectIdentifier = string.IsNullOrEmpty(record.SourceReference)
            ? record.IdempotencyKey
            : record.SourceReference;
        var existingCase = await db.Cases
            .Where(c => c.LocationId == device.LocationId
                        && c.SubjectIdentifier == subjectIdentifier
                        && c.OpenedAt > since
                        && c.State != InspectionWorkflowState.Closed
                        && c.State != InspectionWorkflowState.Cancelled)
            .OrderByDescending(c => c.OpenedAt)
            .FirstOrDefaultAsync(ct);

        InspectionCase parentCase;
        if (existingCase is not null)
        {
            parentCase = existingCase;
        }
        else
        {
            // The worker has no authenticated principal — case opens
            // unattended. Same shape as FS6000's worker path; the case
            // lands with OpenedByUserId=null, the audit emit below
            // captures the system actor.
            var now = DateTimeOffset.UtcNow;
            parentCase = new InspectionCase
            {
                Id = Guid.NewGuid(),
                LocationId = device.LocationId,
                SubjectType = CaseSubjectType.Container, // ASE upstream is container-shaped; future per-instance config can override.
                SubjectIdentifier = subjectIdentifier,
                SubjectPayloadJson = "{}",
                State = InspectionWorkflowState.Open,
                OpenedAt = now,
                StateEnteredAt = now,
                CorrelationId = System.Diagnostics.Activity.Current?.RootId,
                TenantId = device.TenantId
            };
            db.Cases.Add(parentCase);
        }

        // 4. Persist Scan + ScanArtifact. Save the bytes through the
        //    image store so the pre-render worker / image-route handler
        //    can reach back via the content hash.
        var capturedAt = record.CapturedAt;
        var bytes = primary.Bytes;
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var ext = MimeToExtension(primary.MimeType);
        var storageUri = await imageStore.SaveSourceAsync(contentHash, ext, bytes, ct);

        var scan = new Scan
        {
            Id = Guid.NewGuid(),
            CaseId = parentCase.Id,
            ScannerDeviceInstanceId = device.InstanceId,
            Mode = "ingested",
            CapturedAt = capturedAt,
            OperatorUserId = null,
            IdempotencyKey = record.IdempotencyKey,
            CorrelationId = parentCase.CorrelationId
                ?? System.Diagnostics.Activity.Current?.RootId,
            TenantId = device.TenantId
        };
        db.Scans.Add(scan);

        var artifact = new ScanArtifact
        {
            Id = Guid.NewGuid(),
            ScanId = scan.Id,
            ArtifactKind = "Primary",
            StorageUri = storageUri,
            MimeType = primary.MimeType,
            WidthPx = primary.WidthPx,
            HeightPx = primary.HeightPx,
            Channels = primary.Channels,
            ContentHash = contentHash,
            MetadataJson = JsonSerializer.Serialize(primary.Metadata),
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = device.TenantId
        };
        db.ScanArtifacts.Add(artifact);

        try
        {
            await db.SaveChangesAsync(ct);
            AseSyncInstruments.RecordsPersistedTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", device.TypeCode));
        }
        catch (DbUpdateException ex) when (IsScanIdempotencyKeyViolation(ex))
        {
            // Lost the race against another worker/host that beat us
            // to the same idempotency key. Treat as a clean dedupe —
            // the winner's row is canonical.
            _logger.LogDebug(ex,
                "AseSyncWorker lost idempotency-key race for tenant={TenantId} key={Key}; treating as dedupe.",
                device.TenantId, record.IdempotencyKey);
            AseSyncInstruments.DedupedTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", device.TypeCode));
            // Detach so the next iteration's SaveChanges doesn't retry
            // the now-rejected row.
            db.Entry(scan).State = EntityState.Detached;
            db.Entry(artifact).State = EntityState.Detached;
            if (existingCase is null)
            {
                db.Entry(parentCase).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Detects the unique-index violation produced when two concurrent
    /// worker hosts try to persist the same cursor record. Mirror of the
    /// shape <c>CaseWorkflowService.IsScanIdempotencyKeyViolation</c>
    /// uses — kept inline so the worker doesn't take a hard reference
    /// on CaseWorkflowService for this single helper.
    /// </summary>
    private static bool IsScanIdempotencyKeyViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name.Contains("PostgresException", StringComparison.Ordinal) == true
           && ex.InnerException.Message.Contains("ux_scans_tenant_idempotency", StringComparison.Ordinal);

    /// <summary>
    /// Mirror of <c>CaseWorkflowService.MimeToExtension</c>. Inline copy so
    /// the worker doesn't take a hard reference on CaseWorkflowService for
    /// this single helper.
    /// </summary>
    private static string MimeToExtension(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/tiff" => ".tiff",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".bin"
    };

    /// <summary>Lightweight projection over <see cref="ScannerDeviceInstance"/>.</summary>
    private sealed record DeviceDescriptor(
        long TenantId,
        Guid InstanceId,
        Guid LocationId,
        Guid? StationId,
        string TypeCode,
        string ConfigJson);
}

/// <summary>
/// Telemetry instruments for <see cref="AseSyncWorker"/>.
/// </summary>
internal static class AseSyncInstruments
{
    public static readonly System.Diagnostics.Metrics.Counter<long> RecordsPulledTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.scanner.cursor_sync_records_total",
            unit: "records",
            description: "AseSyncWorker count of cursor-sync records pulled.");

    public static readonly System.Diagnostics.Metrics.Counter<long> PullFailedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.scanner.cursor_sync_pull_failed_total",
            unit: "calls",
            description: "AseSyncWorker count of failed PullAsync calls.");

    public static readonly System.Diagnostics.Metrics.Histogram<double> PullLatencyMs =
        NickErpActivity.Meter.CreateHistogram<double>(
            "nickerp.inspection.scanner.cursor_sync_pull_ms",
            unit: "ms",
            description: "AseSyncWorker per-pull wall-clock latency.");

    /// <summary>
    /// Sprint 50 / FU-ase-sync-persistence — Scan + ScanArtifact rows
    /// successfully persisted from cursor pulls. One bump per record.
    /// Tag: <c>type_code</c>.
    /// </summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> RecordsPersistedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.scanner.cursor_sync_records_persisted_total",
            unit: "records",
            description: "AseSyncWorker count of cursor-sync records persisted as Scan + ScanArtifact rows.");

    /// <summary>
    /// Sprint 50 / FU-ase-sync-persistence — cursor records skipped
    /// because the IdempotencyKey already exists (pre-check OR unique-
    /// index race fallback). Tag: <c>type_code</c>.
    /// </summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> DedupedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.scanner.cursor_sync_records_deduped_total",
            unit: "records",
            description: "AseSyncWorker count of cursor-sync records skipped via IdempotencyKey dedupe.");

    /// <summary>
    /// Sprint 50 / FU-ase-sync-persistence — per-record persistence
    /// failures (parse exception, image-store failure, generic DB
    /// error). The cursor still advances on the parent batch unless the
    /// adapter throws on PullAsync. Tag: <c>type_code</c>.
    /// </summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> PersistFailedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.scanner.cursor_sync_persist_failed_total",
            unit: "records",
            description: "AseSyncWorker count of records that threw during Scan persistence.");
}
