using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Database;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Platform.Plugins;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 24 / B3.1 — Scanner health sweep worker. Periodically calls
/// <see cref="IScannerAdapter.TestAsync"/> on every active
/// <see cref="NickERP.Inspection.Core.Entities.ScannerDeviceInstance"/>
/// across every tenant and records the outcome on a telemetry counter
/// (<c>nickerp.inspection.scanner.health_sweep_total</c>).
///
/// <para>
/// Ports v1's <c>FS6000StartupDiagnostics</c> shape — but v1 ran the
/// diagnostic <i>once</i> at startup, then handed off to the file-sync
/// + ingestion services. v2 keeps the diagnostic surface but turns it
/// into a periodic sweep so connectivity loss surfaces without waiting
/// for a real scan to fail (e.g. the scanner SDK lost its license, the
/// watch-folder mount went read-only, the ASE SQL Server password
/// rotated). Vendor-neutral — every <see cref="IScannerAdapter"/>
/// declares <see cref="IScannerAdapter.TestAsync"/>, so FS6000, ASE,
/// future adapters all flow through this loop without per-vendor code.
/// </para>
///
/// <para>
/// <b>Default-disabled.</b> Per Sprint 24 architectural decisions,
/// <see cref="ScannerHealthSweepOptions.Enabled"/> defaults to
/// <c>false</c>. Host operators opt in per environment; until then
/// the loop logs once and exits. Keeps the floor quiet on a fresh
/// deploy where no scanners are configured.
/// </para>
///
/// <para>
/// Mirrors <see cref="ScannerIngestionWorker"/>'s shape: cross-tenant
/// discovery via <see cref="TenancyDbContext"/>; per-iteration scope;
/// plugin singleton + scoped DbContext via
/// <see cref="IServiceScopeFactory"/>; per-instance failure does not
/// kill the worker.
/// </para>
/// </summary>
public sealed class ScannerHealthSweepWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<ScannerHealthSweepOptions> _options;
    private readonly ILogger<ScannerHealthSweepWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public ScannerHealthSweepWorker(
        IServiceProvider services,
        IOptions<ScannerHealthSweepOptions> options,
        ILogger<ScannerHealthSweepWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(ScannerHealthSweepWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "ScannerHealthSweepWorker disabled via {Section}:Enabled=false; not starting.",
                ScannerHealthSweepOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "ScannerHealthSweepWorker starting — sweeping every {Interval}, startup delay {Delay}.",
            opts.PollInterval, opts.StartupDelay);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var swept = await SweepOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (swept > 0)
                {
                    _logger.LogDebug(
                        "ScannerHealthSweepWorker swept {Count} scanner instance(s).", swept);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "ScannerHealthSweepWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One sweep cycle. Walks every active tenant + every active
    /// <see cref="NickERP.Inspection.Core.Entities.ScannerDeviceInstance"/>
    /// in that tenant + invokes the adapter's
    /// <see cref="IScannerAdapter.TestAsync"/> + records the outcome.
    /// Returns the count of (tenant, instance) pairs visited.
    /// </summary>
    /// <remarks>
    /// Internal so the test project can drive a single cycle without
    /// the full hosted-service start dance.
    /// </remarks>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var devices = await DiscoverActiveDevicesAsync(ct);
        if (devices.Count == 0) return 0;

        var swept = 0;
        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await TestOneAsync(device, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ScannerHealthSweepWorker test failed for tenant={TenantId} instance={InstanceId} type={TypeCode}; continuing sweep.",
                    device.TenantId, device.InstanceId, device.TypeCode);
            }
            swept++;
        }

        return swept;
    }

    private async Task<IReadOnlyList<DeviceDescriptor>> DiscoverActiveDevicesAsync(CancellationToken ct)
    {
        var results = new List<DeviceDescriptor>();

        using var discoveryScope = _services.CreateScope();
        var sp = discoveryScope.ServiceProvider;
        var tenantsDb = sp.GetRequiredService<TenancyDbContext>();
        var tenant = sp.GetRequiredService<ITenantContext>();
        var inspectionDb = sp.GetRequiredService<InspectionDbContext>();

        var activeTenantIds = await tenantsDb.Tenants
            .AsNoTracking()
            .Where(t => t.State == TenantState.Active)
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var tenantId in activeTenantIds)
        {
            ct.ThrowIfCancellationRequested();

            tenant.SetTenant(tenantId);

            // Force a fresh connection so the connection interceptor
            // re-pushes app.tenant_id with the new value.
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

            results.AddRange(devices);
        }

        return results;
    }

    private async Task TestOneAsync(DeviceDescriptor device, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var plugins = sp.GetRequiredService<IPluginRegistry>();
        tenant.SetTenant(device.TenantId);

        IScannerAdapter adapter;
        try
        {
            adapter = plugins.Resolve<IScannerAdapter>("inspection", device.TypeCode, sp);
        }
        catch (KeyNotFoundException)
        {
            // No plugin for this type — count once per cycle so the
            // counter shows the misconfiguration, but don't spam logs.
            ScannerHealthSweepInstruments.NoPluginsTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", device.TypeCode));
            return;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex,
                "ScannerHealthSweepWorker: plugin '{TypeCode}' did not resolve cleanly; skipping (tenant={TenantId} instance={InstanceId}).",
                device.TypeCode, device.TenantId, device.InstanceId);
            return;
        }

        var cfg = new ScannerDeviceConfig(
            DeviceId: device.InstanceId,
            LocationId: device.LocationId,
            StationId: device.StationId,
            TenantId: device.TenantId,
            ConfigJson: device.ConfigJson);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ConnectionTestResult result;
        try
        {
            result = await adapter.TestAsync(cfg, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            ScannerHealthSweepInstruments.SweepTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", device.TypeCode),
                new KeyValuePair<string, object?>("result", "exception"));
            ScannerHealthSweepInstruments.SweepLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("type_code", device.TypeCode),
                new KeyValuePair<string, object?>("result", "exception"));
            _logger.LogWarning(ex,
                "ScannerHealthSweepWorker TestAsync threw for tenant={TenantId} instance={InstanceId} type={TypeCode}.",
                device.TenantId, device.InstanceId, device.TypeCode);
            return;
        }

        sw.Stop();
        var resultTag = result.Success ? "ok" : "failed";
        ScannerHealthSweepInstruments.SweepTotal.Add(1,
            new KeyValuePair<string, object?>("type_code", device.TypeCode),
            new KeyValuePair<string, object?>("result", resultTag));
        ScannerHealthSweepInstruments.SweepLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("type_code", device.TypeCode),
            new KeyValuePair<string, object?>("result", resultTag));

        if (!result.Success)
        {
            _logger.LogWarning(
                "ScannerHealthSweepWorker scanner unreachable: tenant={TenantId} instance={InstanceId} type={TypeCode} message={Message}.",
                device.TenantId, device.InstanceId, device.TypeCode, result.Message);
        }
    }

    /// <summary>Lightweight projection over <see cref="NickERP.Inspection.Core.Entities.ScannerDeviceInstance"/>.</summary>
    private sealed record DeviceDescriptor(
        long TenantId,
        Guid InstanceId,
        Guid LocationId,
        Guid? StationId,
        string TypeCode,
        string ConfigJson);
}

/// <summary>
/// Telemetry instruments owned by <see cref="ScannerHealthSweepWorker"/>.
/// Names follow the <c>nickerp.&lt;bounded-context&gt;.&lt;surface&gt;.&lt;unit&gt;</c>
/// convention used elsewhere in the platform.
/// </summary>
internal static class ScannerHealthSweepInstruments
{
    /// <summary>One bump per scanner test. Tags: <c>type_code</c>, <c>result</c> (ok|failed|exception).</summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> SweepTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.scanner.health_sweep_total",
            unit: "tests",
            description: "ScannerHealthSweepWorker per-instance TestAsync invocations.");

    /// <summary>TestAsync wall-clock latency.</summary>
    public static readonly System.Diagnostics.Metrics.Histogram<double> SweepLatencyMs =
        NickErpActivity.Meter.CreateHistogram<double>(
            "nickerp.inspection.scanner.health_sweep_ms",
            unit: "ms",
            description: "ScannerHealthSweepWorker per-instance TestAsync wall-clock latency.");

    /// <summary>Bumped when a scanner has no resolved plugin in the registry.</summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> NoPluginsTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.scanner.health_sweep_no_plugin_total",
            unit: "instances",
            description: "ScannerHealthSweepWorker count of devices with no resolved scanner plugin.");
}
