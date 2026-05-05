using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
/// Sprint 45 / Phase D — periodic queue-tier auto-escalator for the
/// SLA window engine.
///
/// <para>
/// <b>What it does.</b> Walks every active tenant and, for every still-
/// open <see cref="SlaWindow"/>, promotes the
/// <see cref="SlaWindow.QueueTier"/> when the window has been open longer
/// than the per-tier escalation threshold. Promotion ladder:
/// <list type="bullet">
///   <item><description><c>Standard</c> open &gt; 30m → <c>High</c></description></item>
///   <item><description><c>High</c> open &gt; 60m → <c>Urgent</c></description></item>
///   <item><description>All other tiers (<c>Urgent</c>, <c>Exception</c>, <c>PostClearance</c>) terminal — no further auto-escalation.</description></item>
/// </list>
/// Manual operator-set tiers (rows with
/// <see cref="SlaWindow.QueueTierIsManual"/> = true) are never
/// auto-escalated — operators have triaged them, and the worker respects
/// that decision.
/// </para>
///
/// <para>
/// <b>Audit emission.</b> Every promotion emits
/// <c>nickerp.inspection.queue_auto_escalated</c> with the from→to tier
/// pair, window id, case id, age. Idempotent
/// (<see cref="IdempotencyKey"/> derived from window id + new tier) so
/// a re-run after a failed save doesn't double-emit.
/// </para>
///
/// <para>
/// <b>No new system-context caller.</b> Mirrors the
/// <see cref="SlaStateRefresherWorker"/> pattern — enumerate tenants via
/// <see cref="TenancyDbContext.Tenants"/> (no RLS — root of the tenant
/// graph) and run per-tenant scope for the inspection-DB writes.
/// </para>
///
/// <para>
/// <b>Default-disabled</b> per Sprint 24 architectural decision; opt-in
/// per environment via
/// <c>Inspection:Workers:QueueEscalator:Enabled=true</c>.
/// </para>
/// </summary>
public sealed class QueueEscalatorWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<QueueEscalatorOptions> _options;
    private readonly ILogger<QueueEscalatorWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public QueueEscalatorWorker(
        IServiceProvider services,
        IOptions<QueueEscalatorOptions> options,
        ILogger<QueueEscalatorWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(QueueEscalatorWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "QueueEscalatorWorker disabled via {Section}:Enabled=false; not starting.",
                QueueEscalatorOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "QueueEscalatorWorker starting — escalating every {Interval}, startup delay {Delay}, " +
            "Standard→High after {StdToHigh}, High→Urgent after {HighToUrgent}.",
            opts.PollInterval, opts.StartupDelay,
            opts.StandardToHighAfter, opts.HighToUrgentAfter);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var escalated = await EscalateOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (escalated > 0)
                {
                    _logger.LogDebug(
                        "QueueEscalatorWorker escalated {Count} window(s) this tick.", escalated);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "QueueEscalatorWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One escalation cycle. Returns the total count of windows whose
    /// tier was promoted. Internal so tests can drive a single cycle.
    /// </summary>
    internal async Task<int> EscalateOnceAsync(CancellationToken ct)
    {
        var tenantIds = await DiscoverActiveTenantsAsync(ct);
        if (tenantIds.Count == 0) return 0;

        var totalEscalated = 0;
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                totalEscalated += await EscalateTenantAsync(tenantId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "QueueEscalatorWorker failed for tenant={TenantId}; continuing cycle.",
                    tenantId);
            }
        }
        return totalEscalated;
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

    private async Task<int> EscalateTenantAsync(long tenantId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var db = sp.GetRequiredService<InspectionDbContext>();
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

        var asOf = _clock.GetUtcNow();
        var opts = _options.Value;

        // Find auto-escalation candidates in one query: open Standard or
        // High windows older than their tier's threshold, where the tier
        // was NOT manually set by an operator. RLS narrows by tenant on
        // Postgres; the explicit TenantId filter is defense-in-depth and
        // matches AuditNotificationProjector posture.
        var standardCutoff = asOf - opts.StandardToHighAfter;
        var highCutoff = asOf - opts.HighToUrgentAfter;
        var candidates = await db.Set<SlaWindow>()
            .Where(w => w.TenantId == tenantId
                     && w.ClosedAt == null
                     && !w.QueueTierIsManual
                     && (
                            (w.QueueTier == QueueTier.Standard && w.StartedAt <= standardCutoff)
                         || (w.QueueTier == QueueTier.High && w.StartedAt <= highCutoff)
                        ))
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var promotions = new List<(SlaWindow Window, QueueTier From, QueueTier To)>(candidates.Count);
        foreach (var w in candidates)
        {
            var newTier = NextTier(w.QueueTier);
            if (newTier == w.QueueTier) continue;
            var oldTier = w.QueueTier;
            w.QueueTier = newTier;
            promotions.Add((w, oldTier, newTier));
        }

        if (promotions.Count == 0) return 0;

        await db.SaveChangesAsync(ct);

        QueueEscalatorInstruments.WindowsEscalatedTotal.Add(promotions.Count);

        // Audit: one row per promoted window. Idempotency key bound to
        // (window.Id, newTier) so a re-run after a partial failure
        // doesn't double-emit. Best-effort — if audit fails the
        // promotion is still durable (the inspection-side state is the
        // source of truth).
        await EmitEscalationAuditsAsync(sp, tenantId, promotions, asOf, ct);

        return promotions.Count;
    }

    private async Task EmitEscalationAuditsAsync(
        IServiceProvider sp,
        long tenantId,
        IReadOnlyList<(SlaWindow Window, QueueTier From, QueueTier To)> promotions,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        var publisher = sp.GetService<IEventPublisher>();
        if (publisher is null) return;

        foreach (var (w, from, to) in promotions)
        {
            try
            {
                var ageMinutes = (asOf - w.StartedAt).TotalMinutes;
                var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tenantId"] = tenantId,
                    ["slaWindowId"] = w.Id,
                    ["caseId"] = w.CaseId,
                    ["windowName"] = w.WindowName,
                    ["fromTier"] = from.ToString(),
                    ["toTier"] = to.ToString(),
                    ["ageMinutes"] = ageMinutes,
                    ["escalatedAt"] = asOf
                };
                var json = JsonSerializer.SerializeToElement(payload);
                var key = IdempotencyKey.ForEntityChange(
                    tenantId,
                    "inspection.queue_auto_escalated",
                    "SlaWindow",
                    $"{w.Id}|{to}",
                    asOf);
                var evt = DomainEvent.Create(
                    tenantId: tenantId,
                    actorUserId: null,
                    correlationId: null,
                    eventType: "inspection.queue_auto_escalated",
                    entityType: "SlaWindow",
                    entityId: w.Id.ToString(),
                    payload: json,
                    idempotencyKey: key);
                await publisher.PublishAsync(evt, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "QueueEscalatorWorker failed to emit inspection.queue_auto_escalated for window={WindowId} tenant={TenantId}.",
                    w.Id, tenantId);
            }
        }
    }

    /// <summary>
    /// Sprint 45 / Phase D — auto-escalation ladder. <c>Standard</c> →
    /// <c>High</c>; <c>High</c> → <c>Urgent</c>. Other tiers terminal.
    /// </summary>
    internal static QueueTier NextTier(QueueTier current) => current switch
    {
        QueueTier.Standard => QueueTier.High,
        QueueTier.High => QueueTier.Urgent,
        _ => current
    };
}

/// <summary>
/// Telemetry instruments owned by <see cref="QueueEscalatorWorker"/>.
/// </summary>
internal static class QueueEscalatorInstruments
{
    public static readonly System.Diagnostics.Metrics.Counter<long> WindowsEscalatedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.sla.queue_escalated_total",
            unit: "windows",
            description: "QueueEscalatorWorker count of SLA windows whose queue tier was auto-promoted.");
}
