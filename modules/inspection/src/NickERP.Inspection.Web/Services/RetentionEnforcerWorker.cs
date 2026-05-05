using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Retention;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Retention;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 44 / Phase B — periodic retention enforcer. Walks every active
/// tenant + every closed case eligible for the auto-purge surface and
/// reports purge candidates per tenant per tick. Does NOT delete; the
/// worker SURFACES candidates for an operator-driven hard-purge decision
/// that lives outside this sprint (post-pilot) — same posture as Sprint
/// 18 <c>TenantPurgeOrchestrator</c>.
///
/// <para>
/// <b>Eligibility predicate.</b> A case becomes a purge candidate when
/// ALL of the following hold:
/// <list type="bullet">
///   <item><see cref="InspectionCase.LegalHold"/> = false (legal-hold
///   trumps retention class — held cases never enter the candidate
///   surface regardless of class).</item>
///   <item><see cref="InspectionCase.RetentionClass"/> ∈ {
///   <see cref="RetentionClass.Standard"/>,
///   <see cref="RetentionClass.Extended"/> } (Enforcement, Training,
///   LegalHold class never auto-purge — operator-driven release only).
///   </item>
///   <item><see cref="InspectionCase.ClosedAt"/> is non-null AND older
///   than <c>now - retentionDays</c> for the case's class. Open cases
///   are never candidates regardless of how long they have lingered.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// <b>Per-tenant fan-out.</b> Discovers active tenants via
/// <see cref="TenancyDbContext.Tenants"/> (root of the tenant graph;
/// no RLS on the tenant table). For each tenant, sets per-tenant
/// context, force-closes the inspection-DB connection so the
/// <c>TenantConnectionInterceptor</c> re-pushes <c>app.tenant_id</c>
/// on the next open, then queries <see cref="InspectionCase"/> with
/// the eligibility predicate. RLS narrows on Postgres; the explicit
/// <c>TenantId == tenantId</c> filter is defense-in-depth so the
/// in-memory test provider exercises the same query shape. Pattern
/// matches Sprint 36 <see cref="SlaStateRefresherWorker"/>; no
/// <c>SetSystemContext</c> caller introduced.
/// </para>
///
/// <para>
/// <b>Audit emission.</b> Per tenant per tick when the candidate count
/// is &gt; 0, the worker emits
/// <c>nickerp.inspection.retention_purge_candidates_found</c> with
/// <c>{tenantId, candidateCount, perClass, oldestClosedAt,
/// newestClosedAt}</c>. Idle tenants emit nothing — keeps the audit
/// stream quiet.
/// </para>
///
/// <para>
/// <b>Default-disabled</b> per Sprint 24 architectural decision. Hosts
/// opt in via <c>Inspection:Workers:RetentionEnforcer:Enabled=true</c>
/// and tune the cadence via
/// <c>Inspection:Workers:RetentionEnforcer:PollInterval</c> (default
/// 6 hours).
/// </para>
/// </summary>
public sealed class RetentionEnforcerWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<RetentionEnforcerOptions> _options;
    private readonly ILogger<RetentionEnforcerWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public RetentionEnforcerWorker(
        IServiceProvider services,
        IOptions<RetentionEnforcerOptions> options,
        ILogger<RetentionEnforcerWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(RetentionEnforcerWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "RetentionEnforcerWorker disabled via {Section}:Enabled=false; not starting.",
                RetentionEnforcerOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "RetentionEnforcerWorker starting — sweeping every {Interval}, startup delay {Delay}.",
            opts.PollInterval, opts.StartupDelay);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var found = await SweepOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (found > 0)
                {
                    _logger.LogDebug(
                        "RetentionEnforcerWorker found {Count} purge candidate(s) this tick.",
                        found);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "RetentionEnforcerWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One sweep cycle. Walks every active tenant; per tenant, finds
    /// every closed Standard/Extended case past its retention window;
    /// emits the per-tenant audit event when candidates &gt; 0.
    /// Returns the total count of purge candidates surfaced this cycle
    /// (across all tenants).
    /// </summary>
    /// <remarks>Internal so tests can drive a single cycle.</remarks>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var tenantIds = await DiscoverActiveTenantsAsync(ct);
        if (tenantIds.Count == 0) return 0;

        var totalFound = 0;
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                totalFound += await SweepTenantAsync(tenantId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RetentionEnforcerWorker failed for tenant={TenantId}; continuing cycle.",
                    tenantId);
            }
        }
        return totalFound;
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

    private async Task<int> SweepTenantAsync(long tenantId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var db = sp.GetRequiredService<InspectionDbContext>();
        var retention = sp.GetRequiredService<RetentionService>();
        tenant.SetTenant(tenantId);

        // Force a fresh connection so the tenant interceptor re-pushes
        // app.tenant_id with the new value. Same posture as
        // SlaStateRefresherWorker.
        try
        {
            if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                await db.Database.CloseConnectionAsync();
        }
        catch { /* best-effort */ }

        // Resolve the cutoff per eligible class once per tenant per
        // tick — avoid two queries with the same ITenantSettingsService
        // round-trip per case. Only Standard + Extended are eligible
        // for the candidate surface.
        var now = _clock.GetUtcNow();
        var standardPolicy = await retention.GetRetentionPolicyAsync(RetentionClass.Standard, ct);
        var extendedPolicy = await retention.GetRetentionPolicyAsync(RetentionClass.Extended, ct);

        var standardCutoff = SafeCutoff(now, standardPolicy.RetentionDays);
        var extendedCutoff = SafeCutoff(now, extendedPolicy.RetentionDays);

        var standardCount = standardPolicy.IsAutoPurgeEligible
            ? await CountPurgeCandidatesAsync(db, tenantId, RetentionClass.Standard, standardCutoff, ct)
            : 0;
        var extendedCount = extendedPolicy.IsAutoPurgeEligible
            ? await CountPurgeCandidatesAsync(db, tenantId, RetentionClass.Extended, extendedCutoff, ct)
            : 0;

        var totalFound = standardCount + extendedCount;
        RetentionEnforcerInstruments.TenantsScannedTotal.Add(1);
        RetentionEnforcerInstruments.PurgeCandidatesFoundTotal.Add(totalFound);

        if (totalFound > 0)
        {
            // Span of closed-at values across the whole candidate set,
            // so the operator-facing audit-event payload tells "oldest
            // candidate is from <date>" + "newest is from <date>" at a
            // glance.
            var (oldest, newest) = await CandidateClosedRangeAsync(
                db, tenantId, standardCutoff, extendedCutoff, ct);
            await EmitCandidatesFoundAuditAsync(
                sp,
                tenantId,
                standardCount,
                extendedCount,
                oldest,
                newest,
                now,
                ct);
        }

        return totalFound;
    }

    private static DateTimeOffset SafeCutoff(DateTimeOffset now, int retentionDays)
    {
        // int.MaxValue (Training/LegalHold/no-eligible-class) — never
        // any candidate. Push the cutoff into the deep past so the
        // count is always 0; we don't even reach the query for
        // non-eligible classes via IsAutoPurgeEligible filter, but the
        // safe fallback keeps the math defensive.
        if (retentionDays >= int.MaxValue / 2) return DateTimeOffset.MinValue;
        try
        {
            return now.AddDays(-retentionDays);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static async Task<int> CountPurgeCandidatesAsync(
        InspectionDbContext db,
        long tenantId,
        RetentionClass retentionClass,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        if (cutoff == DateTimeOffset.MinValue) return 0;
        return await db.Set<InspectionCase>()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId
                     && !c.LegalHold
                     && c.RetentionClass == retentionClass
                     && c.ClosedAt != null
                     && c.ClosedAt < cutoff)
            .CountAsync(ct);
    }

    private static async Task<(DateTimeOffset? oldest, DateTimeOffset? newest)> CandidateClosedRangeAsync(
        InspectionDbContext db,
        long tenantId,
        DateTimeOffset standardCutoff,
        DateTimeOffset extendedCutoff,
        CancellationToken ct)
    {
        // Project ClosedAt into the runtime's nullable slot; aggregate
        // in-memory across the at-most-two narrow result sets to avoid
        // EF translation gotchas with combined Min/Max + Where in a
        // single query.
        var dates = await db.Set<InspectionCase>()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId
                     && !c.LegalHold
                     && c.ClosedAt != null
                     && (
                         (c.RetentionClass == RetentionClass.Standard && c.ClosedAt < standardCutoff)
                         || (c.RetentionClass == RetentionClass.Extended && c.ClosedAt < extendedCutoff)
                     ))
            .Select(c => c.ClosedAt)
            .ToListAsync(ct);

        if (dates.Count == 0) return (null, null);
        return (dates.Min(), dates.Max());
    }

    private async Task EmitCandidatesFoundAuditAsync(
        IServiceProvider sp,
        long tenantId,
        int standardCount,
        int extendedCount,
        DateTimeOffset? oldestClosedAt,
        DateTimeOffset? newestClosedAt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var publisher = sp.GetService<IEventPublisher>();
        if (publisher is null) return;

        try
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tenantId"] = tenantId,
                ["candidateCount"] = standardCount + extendedCount,
                ["standardCount"] = standardCount,
                ["extendedCount"] = extendedCount,
                ["oldestClosedAt"] = oldestClosedAt,
                ["newestClosedAt"] = newestClosedAt,
                ["asOf"] = now
            };
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(
                tenantId,
                "inspection.retention_purge_candidates_found",
                "Tenant",
                tenantId.ToString(),
                now);
            var evt = DomainEvent.Create(
                tenantId: tenantId,
                actorUserId: null,
                correlationId: null,
                eventType: "nickerp.inspection.retention_purge_candidates_found",
                entityType: "Tenant",
                entityId: tenantId.ToString(),
                payload: json,
                idempotencyKey: key);
            await publisher.PublishAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "RetentionEnforcerWorker failed to emit retention_purge_candidates_found for tenant={TenantId}.",
                tenantId);
        }
    }
}

/// <summary>
/// Telemetry instruments owned by <see cref="RetentionEnforcerWorker"/>.
/// </summary>
internal static class RetentionEnforcerInstruments
{
    public static readonly System.Diagnostics.Metrics.Counter<long> TenantsScannedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.retention.enforcer_tenants_total",
            unit: "tenants",
            description: "RetentionEnforcerWorker count of tenants visited per cycle.");

    public static readonly System.Diagnostics.Metrics.Counter<long> PurgeCandidatesFoundTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.retention.enforcer_candidates_total",
            unit: "cases",
            description: "RetentionEnforcerWorker count of cases surfaced as purge candidates this cycle.");
}
