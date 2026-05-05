using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Webhooks.Abstractions;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Plugins;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 47 / Phase B — periodic outbound-webhook dispatcher. Reads
/// <c>audit.events</c> rows whose <see cref="DomainEventRow.EventType"/>
/// matches the <see cref="WebhookEventTypes"/> standard vocabulary,
/// builds a <see cref="WebhookEvent"/>, and invokes every registered
/// <see cref="IOutboundWebhookAdapter"/> per tenant per tick.
///
/// <para>
/// <b>Per-(tenant, adapter) cursors.</b> The dispatcher tracks one
/// <see cref="WebhookCursor"/> row per (TenantId, AdapterName); two
/// adapters wired for the same tenant advance independently. If a
/// SIEM forwarder is down for an hour, a parallel risk-scoring
/// partner doesn't replay an hour's worth of events when the SIEM
/// recovers. The cursor records the last successfully-dispatched
/// audit event id; the next tick reads forward from there.
/// </para>
///
/// <para>
/// <b>Per-adapter exception isolation.</b> The dispatcher catches
/// every exception thrown from
/// <see cref="IOutboundWebhookAdapter.DispatchAsync"/>; one bad
/// adapter cannot poison dispatch for the others on the same tick.
/// Failed dispatches emit
/// <c>nickerp.webhooks.dispatch_failed</c>; the cursor stays at the
/// prior successful event so the next tick retries.
/// </para>
///
/// <para>
/// <b>Cross-tenant fan-out.</b> The worker iterates active tenants
/// via <see cref="TenancyDbContext.Tenants"/> (no RLS — root of the
/// tenant graph) and sets per-tenant context for the inspection-DB
/// reads. Mirrors <see cref="SlaStateRefresherWorker"/>'s shape — no
/// new <c>SetSystemContext</c> caller. Audit reads happen under
/// system context (matching <see cref="AuditNotificationProjector"/>)
/// because <c>audit.events</c> is the one suite-wide-opt-in table on
/// the audit DB; the LINQ <c>where e.TenantId == tenantId</c> still
/// narrows reads + the WITH CHECK posture is a no-op here (we never
/// write back to audit.events).
/// </para>
///
/// <para>
/// <b>Idempotency.</b> <see cref="WebhookEvent.IdempotencyKey"/> is
/// the audit row's <see cref="DomainEventRow.EventId"/>. Same value
/// across dispatcher retries; downstream subscribers dedupe on this.
/// </para>
///
/// <para>
/// <b>Default-disabled.</b> Per Sprint 24 architectural decisions,
/// <see cref="WebhookDispatchOptions.Enabled"/> defaults to
/// <c>false</c>; ops opts in per environment. A fresh deploy with no
/// adapter plugins wired no-ops cleanly.
/// </para>
/// </summary>
public sealed class WebhookDispatchWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<WebhookDispatchOptions> _options;
    private readonly ILogger<WebhookDispatchWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public WebhookDispatchWorker(
        IServiceProvider services,
        IOptions<WebhookDispatchOptions> options,
        ILogger<WebhookDispatchWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(WebhookDispatchWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "WebhookDispatchWorker disabled via {Section}:Enabled=false; not starting.",
                WebhookDispatchOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "WebhookDispatchWorker starting — polling every {Interval}, startup delay {Delay}, batch limit {Batch}.",
            opts.PollInterval, opts.StartupDelay, opts.BatchLimit);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var dispatched = await DispatchOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (dispatched > 0)
                {
                    _logger.LogDebug(
                        "WebhookDispatchWorker dispatched {Count} (tenant, adapter, event) tuple(s) this tick.",
                        dispatched);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "WebhookDispatchWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One dispatch cycle. Discovers registered adapters via
    /// <see cref="IPluginRegistry.GetContributedTypes(Type)"/>;
    /// no-ops cleanly when no adapters are registered. Walks every
    /// active tenant + every registered adapter; reads new audit
    /// events forward of the (tenant, adapter) cursor; dispatches +
    /// advances the cursor on success. Returns the count of
    /// (tenant, adapter, event) tuples dispatched (success or
    /// failure).
    /// </summary>
    /// <remarks>
    /// Internal so tests can drive a single cycle without the full
    /// hosted-service start dance.
    /// </remarks>
    internal async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        // Enumerate adapter types contributed by registered plugins.
        // GetContributedTypes returns the empty list when no plugin
        // assemblies contain a concrete IOutboundWebhookAdapter — the
        // common case for v2 pre-pilot. Skip the rest of the cycle in
        // that case so we don't open DbContext scopes for nothing.
        IReadOnlyList<Type> adapterTypes;
        using (var bootScope = _services.CreateScope())
        {
            var registry = bootScope.ServiceProvider.GetRequiredService<IPluginRegistry>();
            adapterTypes = registry.GetContributedTypes(typeof(IOutboundWebhookAdapter));
        }

        if (adapterTypes.Count == 0) return 0;

        var tenantIds = await DiscoverActiveTenantsAsync(ct);
        if (tenantIds.Count == 0) return 0;

        var totalDispatched = 0;
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                totalDispatched += await DispatchTenantAsync(tenantId, adapterTypes, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "WebhookDispatchWorker failed for tenant={TenantId}; continuing cycle.",
                    tenantId);
            }
        }

        return totalDispatched;
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

    private async Task<int> DispatchTenantAsync(
        long tenantId,
        IReadOnlyList<Type> adapterTypes,
        CancellationToken ct)
    {
        var opts = _options.Value;
        var dispatched = 0;

        foreach (var adapterType in adapterTypes)
        {
            ct.ThrowIfCancellationRequested();

            // One scope per (tenant, adapter) so each adapter resolves
            // its own scoped collaborators. Reusing a scope across
            // adapters would hide isolation bugs (e.g. adapter A
            // mutating a scoped service that adapter B then sees).
            using var scope = _services.CreateScope();
            var sp = scope.ServiceProvider;
            var tenant = sp.GetRequiredService<ITenantContext>();
            var inspectionDb = sp.GetRequiredService<InspectionDbContext>();
            tenant.SetTenant(tenantId);

            // Force a fresh connection so the tenant interceptor
            // re-pushes app.tenant_id with the new value. Same posture
            // as SlaStateRefresherWorker.
            try
            {
                if (inspectionDb.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                    await inspectionDb.Database.CloseConnectionAsync();
            }
            catch { /* best-effort */ }

            IOutboundWebhookAdapter adapter;
            try
            {
                adapter = (IOutboundWebhookAdapter)sp.GetRequiredService(adapterType);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "WebhookDispatchWorker could not resolve adapter type {AdapterType} for tenant={TenantId}; skipping.",
                    adapterType.FullName, tenantId);
                continue;
            }

            try
            {
                dispatched += await DispatchTenantAdapterAsync(
                    tenantId, adapter, sp, inspectionDb, opts.BatchLimit, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Per-adapter exception isolation. One bad adapter
                // must not stop dispatch for the others on the same
                // tenant. The inner DispatchTenantAdapterAsync catches
                // adapter-level exceptions per event; this outer catch
                // covers infrastructure failures (DbContext, etc.).
                _logger.LogError(ex,
                    "WebhookDispatchWorker failed for tenant={TenantId} adapter={AdapterName}; continuing.",
                    tenantId, adapter.AdapterName);
            }
        }

        return dispatched;
    }

    private async Task<int> DispatchTenantAdapterAsync(
        long tenantId,
        IOutboundWebhookAdapter adapter,
        IServiceProvider sp,
        InspectionDbContext inspectionDb,
        int batchLimit,
        CancellationToken ct)
    {
        // Resolve / create the cursor. The cursor row's primary key
        // (Id Guid) is left to EF; the unique index is on
        // (TenantId, AdapterName) per Sprint 41's DbContext config.
        var cursor = await inspectionDb.WebhookCursors
            .FirstOrDefaultAsync(c => c.AdapterName == adapter.AdapterName && c.TenantId == tenantId, ct);
        if (cursor is null)
        {
            cursor = new WebhookCursor
            {
                Id = Guid.NewGuid(),
                AdapterName = adapter.AdapterName,
                LastProcessedEventId = Guid.Empty,
                UpdatedAt = _clock.GetUtcNow(),
                TenantId = tenantId
            };
            inspectionDb.WebhookCursors.Add(cursor);
            await inspectionDb.SaveChangesAsync(ct);
        }

        var newEvents = await ReadNewAuditEventsAsync(sp, tenantId, cursor, batchLimit, ct);
        if (newEvents.Count == 0) return 0;

        var dispatched = 0;
        var failed = 0;
        Guid lastSuccessId = cursor.LastProcessedEventId;

        foreach (var evtRow in newEvents)
        {
            ct.ThrowIfCancellationRequested();

            var webhookEvt = MapToWebhookEvent(evtRow, tenantId);
            try
            {
                await adapter.DispatchAsync(webhookEvt, ct);
                lastSuccessId = evtRow.EventId;
                dispatched++;
                WebhookDispatchInstruments.DispatchedTotal.Add(1,
                    new KeyValuePair<string, object?>("adapter", adapter.AdapterName),
                    new KeyValuePair<string, object?>("event_type", evtRow.EventType));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                WebhookDispatchInstruments.DispatchFailedTotal.Add(1,
                    new KeyValuePair<string, object?>("adapter", adapter.AdapterName),
                    new KeyValuePair<string, object?>("event_type", evtRow.EventType));
                _logger.LogWarning(ex,
                    "WebhookDispatchWorker adapter={AdapterName} threw on event {EventId} ({EventType}) for tenant={TenantId}; cursor stays + next tick retries.",
                    adapter.AdapterName, evtRow.EventId, evtRow.EventType, tenantId);

                await EmitDispatchFailedAuditAsync(
                    sp, tenantId, adapter.AdapterName, evtRow.EventId,
                    evtRow.EventType, ex, ct);

                // Cursor stays at lastSuccessId so the next tick
                // retries from this event. Stop the inner loop so we
                // don't push past a failed event; an adapter that
                // consistently fails on event N would otherwise
                // appear to make progress on later events while
                // event N is silently lost.
                break;
            }
        }

        // Advance the cursor to the highest successfully-dispatched
        // event id; no-op when nothing succeeded.
        if (lastSuccessId != cursor.LastProcessedEventId)
        {
            cursor.LastProcessedEventId = lastSuccessId;
            cursor.UpdatedAt = _clock.GetUtcNow();
            await inspectionDb.SaveChangesAsync(ct);
        }

        if (dispatched > 0)
        {
            await EmitDispatchedAuditAsync(
                sp, tenantId, adapter.AdapterName, dispatched, failed, ct);
        }

        return dispatched;
    }

    /// <summary>
    /// Read the next batch of <c>audit.events</c> rows for this
    /// tenant after the cursor, filtered to the standard vocabulary.
    /// Ordered by (IngestedAt, EventId) for stable cursor advance —
    /// IngestedAt has 1us granularity on Postgres so two rows with
    /// the same ingested-at land in deterministic order via the
    /// secondary sort.
    /// </summary>
    private static async Task<IReadOnlyList<DomainEventRow>> ReadNewAuditEventsAsync(
        IServiceProvider sp,
        long tenantId,
        WebhookCursor cursor,
        int batchLimit,
        CancellationToken ct)
    {
        var auditDb = sp.GetRequiredService<AuditDbContext>();
        var tenant = sp.GetRequiredService<ITenantContext>();

        // System context for audit.events — matches AuditNotificationProjector.
        // The LINQ TenantId == tenantId filter narrows reads + keeps
        // the query plan tight; RLS on audit.events is permissive
        // under system context per docs/system-context-audit-register.md.
        tenant.SetSystemContext();
        try
        {
            if (auditDb.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                await auditDb.Database.CloseConnectionAsync();
        }
        catch { /* best-effort */ }

        // Subset of audit-event types this dispatcher cares about;
        // materialised as a string[] so EF's
        // <c>contains</c> translation works on both Postgres + the
        // EF in-memory provider used in tests (the latter doesn't
        // know how to lift IReadOnlySet&lt;T&gt;.Contains).
        var subscribedTypes = StandardEventTypesArray;

        IQueryable<DomainEventRow> q = auditDb.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                && subscribedTypes.Contains(e.EventType));

        // Cursor advance — only events strictly after the last
        // processed one. Compare-by-EventId is fine because the
        // cursor stores the last successfully-dispatched id; the
        // sentinel Guid.Empty matches the "first tick" case where
        // we read from the start of the audit stream for the tenant.
        if (cursor.LastProcessedEventId != Guid.Empty)
        {
            // Find the IngestedAt of the cursor row so we can scan
            // forward from there. Two-stage query because EF can't
            // cleanly express "rows where (IngestedAt, EventId) >
            // cursor (IngestedAt, EventId)".
            var cursorRow = await auditDb.Events
                .AsNoTracking()
                .Where(e => e.EventId == cursor.LastProcessedEventId)
                .Select(e => new { e.EventId, e.IngestedAt })
                .FirstOrDefaultAsync(ct);

            if (cursorRow is not null)
            {
                var cursorIngestedAt = cursorRow.IngestedAt;
                var cursorId = cursorRow.EventId;
                q = q.Where(e =>
                    e.IngestedAt > cursorIngestedAt
                    || (e.IngestedAt == cursorIngestedAt && string.Compare(e.EventId.ToString(), cursorId.ToString()) > 0));
            }
        }

        return await q
            .OrderBy(e => e.IngestedAt)
            .ThenBy(e => e.EventId)
            .Take(batchLimit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Map an <see cref="DomainEventRow"/> to a
    /// <see cref="WebhookEvent"/>. Caller must have verified the
    /// event type belongs to <see cref="WebhookEventTypes"/>.
    /// Public static so tests can drive the mapper without spinning
    /// up the full DI graph; production callers go through
    /// <see cref="DispatchOnceAsync"/>.
    /// </summary>
    public static WebhookEvent MapToWebhookEvent(DomainEventRow row, long tenantId)
    {
        // Payload deserialise — best-effort. Audit.events stores JSON
        // documents per the publisher contract; we lift the top-level
        // properties into a vendor-neutral dictionary so adapters
        // don't have to reach back into JsonElement to read.
        var payload = new Dictionary<string, object>(StringComparer.Ordinal);
        if (row.Payload is not null)
        {
            try
            {
                using var json = row.Payload;
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in json.RootElement.EnumerateObject())
                    {
                        payload[prop.Name] = ConvertJsonValue(prop.Value);
                    }
                }
            }
            catch (Exception)
            {
                // Defensive — corrupt payload shouldn't crash the
                // dispatcher. The mapped event ships with an empty
                // payload + an explicit "_payload_unparseable" marker
                // so adapters can decide how to handle it.
                payload["_payload_unparseable"] = true;
            }
        }

        Guid? entityId = null;
        if (Guid.TryParse(row.EntityId, out var parsedGuid))
        {
            entityId = parsedGuid;
        }

        return new WebhookEvent(
            EventType: row.EventType,
            TenantId: tenantId,
            EntityId: entityId,
            EntityType: row.EntityType,
            Payload: payload,
            OccurredAt: row.OccurredAt,
            IdempotencyKey: row.EventId);
    }

    private static object ConvertJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null!,
        _ => value.ToString()
    };

    private async Task EmitDispatchedAuditAsync(
        IServiceProvider sp, long tenantId, string adapterName,
        int dispatchedCount, int failedCount, CancellationToken ct)
    {
        var publisher = sp.GetService<IEventPublisher>();
        if (publisher is null) return;

        try
        {
            var asOf = _clock.GetUtcNow();
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tenantId"] = tenantId,
                ["adapter"] = adapterName,
                ["dispatched"] = dispatchedCount,
                ["failed"] = failedCount,
                ["asOf"] = asOf
            };
            var json = JsonSerializer.SerializeToElement(payload);
            var key = NickERP.Platform.Audit.IdempotencyKey.ForEntityChange(
                tenantId, "nickerp.webhooks.dispatched", "WebhookAdapter",
                adapterName, asOf);
            var evt = DomainEvent.Create(
                tenantId: tenantId,
                actorUserId: null,
                correlationId: null,
                eventType: "nickerp.webhooks.dispatched",
                entityType: "WebhookAdapter",
                entityId: adapterName,
                payload: json,
                idempotencyKey: key);
            await publisher.PublishAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "WebhookDispatchWorker failed to emit nickerp.webhooks.dispatched for tenant={TenantId} adapter={AdapterName}.",
                tenantId, adapterName);
        }
    }

    private async Task EmitDispatchFailedAuditAsync(
        IServiceProvider sp, long tenantId, string adapterName,
        Guid failedEventId, string failedEventType, Exception ex, CancellationToken ct)
    {
        var publisher = sp.GetService<IEventPublisher>();
        if (publisher is null) return;

        try
        {
            var asOf = _clock.GetUtcNow();
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tenantId"] = tenantId,
                ["adapter"] = adapterName,
                ["eventId"] = failedEventId,
                ["eventType"] = failedEventType,
                ["error"] = ex.GetType().Name,
                ["message"] = ex.Message,
                ["asOf"] = asOf
            };
            var json = JsonSerializer.SerializeToElement(payload);
            var key = NickERP.Platform.Audit.IdempotencyKey.From(
                tenantId, "nickerp.webhooks.dispatch_failed",
                adapterName, failedEventId.ToString());
            var evt = DomainEvent.Create(
                tenantId: tenantId,
                actorUserId: null,
                correlationId: null,
                eventType: "nickerp.webhooks.dispatch_failed",
                entityType: "WebhookAdapter",
                entityId: adapterName,
                payload: json,
                idempotencyKey: key);
            await publisher.PublishAsync(evt, ct);
        }
        catch (Exception emitEx) when (emitEx is not OperationCanceledException)
        {
            _logger.LogWarning(emitEx,
                "WebhookDispatchWorker failed to emit nickerp.webhooks.dispatch_failed for tenant={TenantId} adapter={AdapterName} eventId={EventId}.",
                tenantId, adapterName, failedEventId);
        }
    }

    /// <summary>
    /// Static set of standard <see cref="WebhookEventTypes"/> string
    /// constants. Materialised once so the audit-event LINQ query
    /// can <c>.Contains</c> against a deterministic set without
    /// reflecting on every tick. Public static so tests can assert
    /// drift against the constants in
    /// <see cref="WebhookEventTypes"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> StandardEventTypeSet = new HashSet<string>(StringComparer.Ordinal)
    {
        WebhookEventTypes.HIGH_RISK_SCAN_DETECTED,
        WebhookEventTypes.INSPECTION_REQUIRED,
        WebhookEventTypes.SCAN_REVIEWED,
        WebhookEventTypes.CASE_CREATED,
        WebhookEventTypes.GATEWAY_OFFLINE,
        WebhookEventTypes.SCANNER_OFFLINE,
        WebhookEventTypes.AI_MODEL_DRIFT_ALERT,
        WebhookEventTypes.LEGAL_HOLD_APPLIED,
        WebhookEventTypes.LEGAL_HOLD_RELEASED,
        WebhookEventTypes.THRESHOLD_CHANGED
    };

    /// <summary>
    /// Same content as <see cref="StandardEventTypeSet"/> but as a
    /// concrete <c>string[]</c> so EF's
    /// <c>Where(... arr.Contains(col) ...)</c> translates on both
    /// Npgsql + the EF in-memory provider (the latter doesn't lift
    /// <see cref="IReadOnlySet{T}.Contains(T)"/>).
    /// </summary>
    internal static readonly string[] StandardEventTypesArray = StandardEventTypeSet.ToArray();
}

/// <summary>
/// Telemetry instruments owned by <see cref="WebhookDispatchWorker"/>.
/// </summary>
internal static class WebhookDispatchInstruments
{
    /// <summary>One bump per successful dispatch. Tags: <c>adapter</c>, <c>event_type</c>.</summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> DispatchedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.webhooks.dispatched_total",
            unit: "events",
            description: "WebhookDispatchWorker count of successfully-dispatched (adapter, event) pairs.");

    /// <summary>One bump per failed dispatch. Tags: <c>adapter</c>, <c>event_type</c>.</summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> DispatchFailedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.webhooks.dispatch_failed_total",
            unit: "events",
            description: "WebhookDispatchWorker count of dispatches that threw or were rejected by the adapter.");
}
