using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 36 / FU-sla-state-refresher-worker — periodic state-refresher
/// worker for the Sprint 31 SLA window engine. The dashboard service
/// already calls
/// <see cref="ISlaTracker.RefreshStatesAsync(Guid, DateTimeOffset, CancellationToken)"/>
/// on every dashboard query path so the screen reflects "as-of-now"
/// lifecycle bucket (OnTime / AtRisk / Breached). That path keeps the
/// dashboard fresh but never writes the transition back — meaning the
/// audit-event / notification path that fires on Breached transitions
/// never trips until somebody loads the dashboard.
///
/// <para>
/// This worker promotes the refresh into a periodic
/// <see cref="BackgroundService"/> that scans every active tenant +
/// every still-open window's case + writes the recomputed state back
/// via the existing <see cref="ISlaTracker.RefreshStatesAsync"/> API.
/// Audit event <c>inspection.sla.state_refreshed</c> fires per tenant
/// per tick when at least one window flipped state.
/// </para>
///
/// <para>
/// <b>No new system-context caller.</b> The worker iterates tenants via
/// <see cref="TenancyDbContext.Tenants"/> (no RLS — root of the tenant
/// graph) and sets per-tenant context for the inspection-DB reads;
/// <c>inspection.sla_window</c> RLS narrows naturally per-tenant.
/// Cross-tenant discovery of "tenants with open windows" via
/// system-context was considered but rejected — it would require an
/// <c>OR app.tenant_id = '-1'</c> opt-in clause on
/// <c>tenant_isolation_sla_window</c>, broadening the table's read
/// surface, for marginal efficiency gain (an extra "is the tenant
/// active?" check per tick on a small table is cheap). Pattern matches
/// <see cref="ScannerHealthSweepWorker"/>.
/// </para>
///
/// <para>
/// <b>Default-disabled</b> per Sprint 24 architectural decision; opt-in
/// per environment via
/// <c>Inspection:Workers:SlaStateRefresher:Enabled=true</c>.
/// </para>
/// </summary>
public sealed class SlaStateRefresherWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<SlaStateRefresherOptions> _options;
    private readonly ILogger<SlaStateRefresherWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public SlaStateRefresherWorker(
        IServiceProvider services,
        IOptions<SlaStateRefresherOptions> options,
        ILogger<SlaStateRefresherWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(SlaStateRefresherWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "SlaStateRefresherWorker disabled via {Section}:Enabled=false; not starting.",
                SlaStateRefresherOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "SlaStateRefresherWorker starting — refreshing every {Interval}, startup delay {Delay}.",
            opts.PollInterval, opts.StartupDelay);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var refreshed = await RefreshOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (refreshed > 0)
                {
                    _logger.LogDebug(
                        "SlaStateRefresherWorker refreshed state on {Count} window(s) this tick.",
                        refreshed);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "SlaStateRefresherWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One refresh cycle. Walks every active tenant, finds every case
    /// that has at least one still-open <see cref="SlaWindow"/>, and
    /// calls <see cref="ISlaTracker.RefreshStatesAsync"/> per case.
    /// Returns the total count of windows whose state flipped.
    /// </summary>
    /// <remarks>Internal so tests can drive a single cycle.</remarks>
    internal async Task<int> RefreshOnceAsync(CancellationToken ct)
    {
        var tenantIds = await DiscoverActiveTenantsAsync(ct);
        if (tenantIds.Count == 0) return 0;

        var totalFlipped = 0;
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                totalFlipped += await RefreshTenantAsync(tenantId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SlaStateRefresherWorker failed for tenant={TenantId}; continuing cycle.",
                    tenantId);
            }
        }
        return totalFlipped;
    }

    private async Task<IReadOnlyList<long>> DiscoverActiveTenantsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Tenants
            .AsNoTracking()
            .Where(t => t.State == TenantState.Active)
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    private async Task<int> RefreshTenantAsync(long tenantId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var db = sp.GetRequiredService<InspectionDbContext>();
        var tracker = sp.GetRequiredService<ISlaTracker>();
        tenant.SetTenant(tenantId);

        // Force a fresh connection so the tenant interceptor re-pushes
        // app.tenant_id with the new value. Same posture as
        // ScannerHealthSweepWorker.
        try
        {
            if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                await db.Database.CloseConnectionAsync();
        }
        catch { /* best-effort */ }

        // Distinct case ids with open windows for THIS tenant. RLS
        // narrows on Postgres; the explicit TenantId filter is
        // defense-in-depth (matches AuditNotificationProjector posture)
        // and lets the EF in-memory provider exercise the same query
        // shape under test.
        var caseIds = await db.Set<SlaWindow>()
            .AsNoTracking()
            .Where(w => w.ClosedAt == null && w.TenantId == tenantId)
            .Select(w => w.CaseId)
            .Distinct()
            .ToListAsync(ct);

        if (caseIds.Count == 0) return 0;

        var asOf = _clock.GetUtcNow();
        var totalFlipped = 0;
        foreach (var caseId in caseIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var flipped = await tracker.RefreshStatesAsync(caseId, asOf, ct);
                totalFlipped += flipped;
                SlaStateRefresherInstruments.WindowsFlippedTotal.Add(flipped);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SlaStateRefresherWorker RefreshStatesAsync failed for case={CaseId} tenant={TenantId}; continuing.",
                    caseId, tenantId);
            }
        }

        SlaStateRefresherInstruments.TenantsScannedTotal.Add(1);

        // Audit emission — only when at least one window flipped; saves
        // audit-row noise on idle tenants (most ticks no row flips).
        if (totalFlipped > 0)
        {
            await EmitStateRefreshedAuditAsync(sp, tenantId, totalFlipped, asOf, ct);
        }

        return totalFlipped;
    }

    private async Task EmitStateRefreshedAuditAsync(
        IServiceProvider sp, long tenantId, int flipped, DateTimeOffset asOf, CancellationToken ct)
    {
        var publisher = sp.GetService<IEventPublisher>();
        if (publisher is null) return;

        try
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tenantId"] = tenantId,
                ["windowsFlipped"] = flipped,
                ["asOf"] = asOf
            };
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(
                tenantId, "inspection.sla.state_refreshed", "Tenant",
                tenantId.ToString(), asOf);
            var evt = DomainEvent.Create(
                tenantId: tenantId,
                actorUserId: null,
                correlationId: null,
                eventType: "inspection.sla.state_refreshed",
                entityType: "Tenant",
                entityId: tenantId.ToString(),
                payload: json,
                idempotencyKey: key);
            await publisher.PublishAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "SlaStateRefresherWorker failed to emit inspection.sla.state_refreshed for tenant={TenantId}.",
                tenantId);
        }
    }
}

/// <summary>
/// Telemetry instruments owned by <see cref="SlaStateRefresherWorker"/>.
/// </summary>
internal static class SlaStateRefresherInstruments
{
    public static readonly System.Diagnostics.Metrics.Counter<long> TenantsScannedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.sla.state_refresher_tenants_total",
            unit: "tenants",
            description: "SlaStateRefresherWorker count of tenants visited per cycle.");

    public static readonly System.Diagnostics.Metrics.Counter<long> WindowsFlippedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.sla.state_refresher_windows_flipped_total",
            unit: "windows",
            description: "SlaStateRefresherWorker count of SLA windows whose state flipped this cycle.");
}
