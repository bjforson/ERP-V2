using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Background service that drives every registered <see cref="ScannerDeviceInstance"/>
/// through its <see cref="IScannerAdapter.StreamAsync"/>. Each emitted
/// <see cref="RawScanArtifact"/> creates (or reuses) a case and ingests the
/// scan via <see cref="CaseWorkflowService.IngestRawArtifactAsync"/>.
///
/// Discovery walks every active tenant (read from <see cref="TenancyDbContext"/>,
/// which is intentionally not under RLS) and queries
/// <c>inspection.scanner_device_instances</c> with that tenant pushed to
/// <c>app.tenant_id</c>; the inspection RLS policies then narrow the result
/// to the rows owned by that tenant. This is the only sane way to cross the
/// tenancy boundary inside a single host process — the alternative
/// (BYPASSRLS connections) would punch a hole in the F1 defense-in-depth.
///
/// Idempotency is content-addressed: <see cref="Scan.IdempotencyKey"/> hashes
/// the parsed source bytes, so re-ingesting the same triplet (e.g. after a
/// host restart that re-walks the watch folder) is a silent no-op caught by
/// the unique index <c>ux_scans_tenant_idempotency</c>.
///
/// Per-instance loops run as independent <see cref="Task"/>s; one slow or
/// crashing scanner doesn't block the others. Loops terminate on the
/// host-shutdown <see cref="CancellationToken"/>.
///
/// Single-host-only for now — running multiple host replicas would race on
/// the watch folder. The SQL-backed durable queue (ARCHITECTURE §7.7) lifts
/// this restriction in a later sprint.
/// </summary>
public sealed class ScannerIngestionWorker : BackgroundService
{
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<ScannerIngestionWorker> _logger;

    public ScannerIngestionWorker(
        IServiceProvider services,
        ILogger<ScannerIngestionWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ScannerIngestionWorker starting — discovering active instances every {Interval}s.",
            DiscoveryInterval.TotalSeconds);

        // Track one per-instance loop task per device id. New instances picked
        // up on the next discovery pass without a host restart; instances
        // that disappear (IsActive=false / row deleted) stop emitting on the
        // next inner iteration of their loop.
        var perInstanceTasks = new Dictionary<Guid, Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var discovered = await DiscoverActiveInstancesAsync(stoppingToken);

                foreach (var (instanceId, tenantId) in discovered)
                {
                    if (perInstanceTasks.TryGetValue(instanceId, out var existing) && !existing.IsCompleted)
                        continue;

                    perInstanceTasks[instanceId] = Task.Run(
                        () => StreamForInstanceAsync(instanceId, tenantId, stoppingToken),
                        stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ScannerIngestionWorker discovery failed; will retry in {Interval}s.",
                    DiscoveryInterval.TotalSeconds);
            }

            try { await Task.Delay(DiscoveryInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// Walk every active tenant and collect the active scanner instances
    /// they own. Returns (instanceId, tenantId) pairs so the per-instance
    /// loop can re-set the tenant context after its own scope rebuild.
    /// </summary>
    private async Task<IReadOnlyList<(Guid InstanceId, long TenantId)>> DiscoverActiveInstancesAsync(
        CancellationToken ct)
    {
        var results = new List<(Guid, long)>();

        // Discovery scope is a different scope from the per-instance loops
        // — its DbContext / tenant context don't outlive this call.
        using var discoveryScope = _services.CreateScope();
        var sp = discoveryScope.ServiceProvider;
        var tenantsDb = sp.GetRequiredService<TenancyDbContext>();
        var tenant = sp.GetRequiredService<ITenantContext>();
        var inspectionDb = sp.GetRequiredService<InspectionDbContext>();

        // tenancy.tenants is the root of the tenancy graph and intentionally
        // NOT under RLS (per Add_RLS_Policies in the tenancy schema), so this
        // read returns rows even with app.tenant_id='0' on the connection.
        var activeTenantIds = await tenantsDb.Tenants
            .AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var tenantId in activeTenantIds)
        {
            ct.ThrowIfCancellationRequested();

            // Push the tenant down for this iteration so RLS lets us see
            // the rows owned by this tenant. The interceptor re-pushes
            // app.tenant_id on the next connection open.
            tenant.SetTenant(tenantId);

            // Force a fresh connection so the connection interceptor fires
            // and re-runs SET app.tenant_id with the new value. EF Core's
            // pooled connection might otherwise reuse a connection still
            // carrying the previous tenant.
            try
            {
                if (inspectionDb.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                    await inspectionDb.Database.CloseConnectionAsync();
            }
            catch
            {
                // Best-effort — if Close fails, the next query will open
                // a new pooled connection anyway and the interceptor sets
                // the tenant.
            }

            var instances = await inspectionDb.ScannerDeviceInstances
                .AsNoTracking()
                .Where(d => d.IsActive)
                .Select(d => d.Id)
                .ToListAsync(ct);

            foreach (var id in instances)
                results.Add((id, tenantId));
        }

        return results;
    }

    /// <summary>
    /// Long-running per-instance pump. Resolves the adapter, builds the
    /// <see cref="ScannerDeviceConfig"/>, and consumes
    /// <see cref="IScannerAdapter.StreamAsync"/> until cancellation. Outer
    /// try/catch is "log + back off + restart" so a transient adapter
    /// exception (e.g. WatchPath went missing for a few seconds) doesn't
    /// kill the loop forever.
    /// </summary>
    private async Task StreamForInstanceAsync(Guid instanceId, long ownerTenantId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // New scope per outer iteration. DbContext + ITenantContext
                // are scoped — keeping them for the lifetime of the inner
                // StreamAsync keeps the tenant set across every IngestArtifact
                // call without re-resolving the scope per artifact, but they
                // get rebuilt on each restart so change-tracking + connection
                // state never grow unbounded.
                using var scope = _services.CreateScope();
                var workflow = scope.ServiceProvider.GetRequiredService<CaseWorkflowService>();
                var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
                var plugins = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

                // Set the tenant FIRST — every subsequent DbContext call goes
                // through the connection interceptor and needs app.tenant_id
                // pushed before RLS lets us read.
                tenant.SetTenant(ownerTenantId);

                ScannerDeviceInstance? instance;
                try
                {
                    instance = await db.ScannerDeviceInstances
                        .AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == instanceId && d.IsActive, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "ScannerIngestionWorker could not load instance {Id}; will retry in {Backoff}s.",
                        instanceId, RestartBackoff.TotalSeconds);
                    try { await Task.Delay(RestartBackoff, ct); }
                    catch (TaskCanceledException) { return; }
                    continue;
                }

                if (instance is null)
                {
                    _logger.LogInformation(
                        "ScannerIngestionWorker instance {Id} no longer active; stopping per-instance loop.",
                        instanceId);
                    return;
                }

                // Defensive — the row's TenantId should match the tenant we
                // discovered it under, but RLS is the load-bearing guard;
                // log + abort if the assumption ever breaks.
                if (instance.TenantId != ownerTenantId)
                {
                    _logger.LogError(
                        "ScannerIngestionWorker tenant mismatch on instance {Id}: discovered under {Owner} but row says {Actual}; aborting loop.",
                        instance.Id, ownerTenantId, instance.TenantId);
                    return;
                }

                IScannerAdapter adapter;
                try
                {
                    adapter = plugins.Resolve<IScannerAdapter>("inspection", instance.TypeCode, scope.ServiceProvider);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ScannerIngestionWorker could not resolve adapter '{TypeCode}' for instance {Id}; will retry in {Backoff}s.",
                        instance.TypeCode, instance.Id, RestartBackoff.TotalSeconds);
                    try { await Task.Delay(RestartBackoff, ct); }
                    catch (TaskCanceledException) { return; }
                    continue;
                }

                var config = new ScannerDeviceConfig(
                    DeviceId: instance.Id,
                    LocationId: instance.LocationId,
                    StationId: instance.StationId,
                    TenantId: instance.TenantId,
                    ConfigJson: instance.ConfigJson);

                _logger.LogInformation(
                    "ScannerIngestionWorker streaming for instance {Id} ({TypeCode}) tenant {TenantId}.",
                    instance.Id, instance.TypeCode, instance.TenantId);

                await foreach (var raw in adapter.StreamAsync(config, ct))
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        await workflow.IngestRawArtifactAsync(instance, adapter, raw, ct);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        // One bad artifact must not kill the loop — log and
                        // keep consuming. The adapter's StreamAsync owns
                        // emitting the next file regardless.
                        _logger.LogError(ex,
                            "Failed to ingest artifact {Path} from instance {Id}; continuing.",
                            raw.SourcePath, instance.Id);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ScannerIngestionWorker loop crashed for instance {Id}; restart in {Backoff}s.",
                    instanceId, RestartBackoff.TotalSeconds);
                try { await Task.Delay(RestartBackoff, ct); }
                catch (TaskCanceledException) { return; }
            }
        }
    }
}
