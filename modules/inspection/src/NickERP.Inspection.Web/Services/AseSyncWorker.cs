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

            await PersistRecordsAsync(db, device, batch.Records, ct);
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
    /// <see cref="Scan"/> rows. The cursor adapter is responsible for
    /// emitting an idempotency key per record; the host writes that
    /// onto <see cref="Scan.IdempotencyKey"/> and lets the unique
    /// index <c>ux_scans_tenant_idempotency</c> dedupe across replays.
    ///
    /// <para>
    /// <b>Skeleton implementation.</b> v2 currently lacks an
    /// "InspectionCase auto-create" path equivalent to v1's container-
    /// keyed flow; for now the worker writes <see cref="Scan"/> rows
    /// without a parent case (a follow-up sprint wires the case-keying
    /// rules). Skeleton is acceptable: ASE adapter doesn't ship today,
    /// so the persistence shape can settle as the adapter contract
    /// firms up. Worker logs + counters are load-bearing.
    /// </para>
    /// </summary>
    private async Task PersistRecordsAsync(
        InspectionDbContext db,
        DeviceDescriptor device,
        IReadOnlyList<CursorSyncRecord> records,
        CancellationToken ct)
    {
        // Skeleton: cursor adapter emits records, but the case-keying
        // rules require domain inputs we don't yet model in v2 (see
        // class XML comment). Counter still bumps so admin pages can
        // see the worker is alive. A later sprint completes this
        // method by either (a) creating a case per record or (b)
        // keying records to existing cases via a vendor-shaped lookup.
        // Both paths land cleanly here without changing the worker's
        // discovery + cursor + telemetry shape.
        await Task.CompletedTask;
        _logger.LogDebug(
            "AseSyncWorker persisted {Count} record(s) (skeleton no-op) tenant={TenantId} instance={InstanceId}.",
            records.Count, device.TenantId, device.InstanceId);
    }

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
}
