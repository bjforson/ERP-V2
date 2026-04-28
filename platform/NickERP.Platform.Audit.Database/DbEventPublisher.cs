using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit.Events;

namespace NickERP.Platform.Audit.Database;

/// <summary>
/// Default <see cref="IEventPublisher"/> — writes <see cref="DomainEvent"/>s
/// to <c>audit.events</c> via <see cref="AuditDbContext"/>. Honors
/// idempotency: a duplicate <see cref="DomainEvent.IdempotencyKey"/> in the
/// same tenant returns the existing row instead of inserting again.
/// </summary>
/// <remarks>
/// LISTEN/NOTIFY dispatch lives in <see cref="InProcessEventBus"/> (a
/// separate concern); the publisher invokes the bus after a successful
/// write so subscribers see freshly-persisted events. If the bus throws,
/// the event has still been recorded — subscribers can re-fetch from the
/// audit table later.
/// </remarks>
internal sealed class DbEventPublisher : IEventPublisher
{
    private readonly AuditDbContext _db;
    private readonly IEventBus _bus;
    private readonly ILogger<DbEventPublisher> _logger;
    private readonly TimeProvider _clock;

    public DbEventPublisher(
        AuditDbContext db,
        IEventBus bus,
        ILogger<DbEventPublisher> logger,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Idempotency dedup inside the same tenant. NULL-tenant
        // (system) events dedupe in their own bucket via
        // (TenantId IS NULL, IdempotencyKey).
        var existing = evt.TenantId is null
            ? await _db.Events
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == null && r.IdempotencyKey == evt.IdempotencyKey, ct)
            : await _db.Events
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == evt.TenantId && r.IdempotencyKey == evt.IdempotencyKey, ct);
        if (existing is not null)
        {
            _logger.LogDebug(
                "Idempotency hit for tenant {Tenant}, key {Key}; returning existing event {EventId}",
                evt.TenantId, evt.IdempotencyKey, existing.EventId);
            return ToDomain(existing);
        }

        var row = new DomainEventRow
        {
            EventId = evt.EventId == Guid.Empty ? Guid.NewGuid() : evt.EventId,
            TenantId = evt.TenantId,
            ActorUserId = evt.ActorUserId,
            CorrelationId = evt.CorrelationId,
            EventType = evt.EventType,
            EntityType = evt.EntityType,
            EntityId = evt.EntityId,
            Payload = JsonDocument.Parse(evt.Payload.GetRawText()),
            OccurredAt = evt.OccurredAt,
            IngestedAt = _clock.GetUtcNow(),
            IdempotencyKey = evt.IdempotencyKey,
            PrevEventHash = evt.PrevEventHash
        };

        _db.Events.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race — concurrent insert with the same idempotency key won. Fetch the winner.
            _logger.LogDebug(ex, "Idempotency race for tenant {Tenant}, key {Key}; fetching winner", evt.TenantId, evt.IdempotencyKey);
            _db.Entry(row).State = EntityState.Detached;
            var winner = evt.TenantId is null
                ? await _db.Events
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.TenantId == null && r.IdempotencyKey == evt.IdempotencyKey, ct)
                : await _db.Events
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.TenantId == evt.TenantId && r.IdempotencyKey == evt.IdempotencyKey, ct);
            if (winner is null) throw;
            return ToDomain(winner);
        }

        var persisted = ToDomain(row);

        // Best-effort dispatch — publisher's responsibility ends with the durable write.
        try
        {
            var channel = ChannelFor(persisted.EventType);
            await _bus.PublishAsync(channel, persisted, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "In-process bus publish failed for {EventType} {EventId}; event is persisted, subscribers may miss live delivery", persisted.EventType, persisted.EventId);
        }

        return persisted;
    }

    public async Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        var output = new List<DomainEvent>(events.Count);
        foreach (var evt in events)
        {
            output.Add(await PublishAsync(evt, ct));
        }
        return output;
    }

    /// <summary>
    /// Channel name = the <em>module</em> segment of the event type
    /// (e.g. <c>nickerp.finance.transaction_recorded</c> →
    /// <c>nickerp.finance</c>; <c>nickerp.inspection.case_opened</c> →
    /// <c>nickerp.inspection</c>). G1 — earlier the channel was the
    /// first segment only (always <c>nickerp</c>), so every subscriber
    /// got every module's events and had to filter in-process.
    /// Subscribers register against the two-segment prefix; events that
    /// don't have a module (single-segment <c>"foo"</c>) keep the bare
    /// segment as their channel.
    /// </summary>
    internal static string ChannelFor(string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return eventType;
        var firstDot = eventType.IndexOf('.', StringComparison.Ordinal);
        if (firstDot < 0) return eventType;
        var secondDot = eventType.IndexOf('.', firstDot + 1);
        return secondDot < 0 ? eventType : eventType[..secondDot];
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Npgsql wraps PG SQLSTATE 23505 inner exceptions; check by message + sqlstate via reflection-free path.
        var inner = ex.InnerException?.Message ?? string.Empty;
        return inner.Contains("23505", StringComparison.Ordinal)
            || inner.Contains("ux_audit_events_tenant_idempotency", StringComparison.OrdinalIgnoreCase);
    }

    private static DomainEvent ToDomain(DomainEventRow r)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(r.Payload.RootElement.GetRawText());
        return new DomainEvent(
            EventId: r.EventId,
            TenantId: r.TenantId,
            ActorUserId: r.ActorUserId,
            CorrelationId: r.CorrelationId,
            EventType: r.EventType,
            EntityType: r.EntityType,
            EntityId: r.EntityId,
            Payload: payload,
            OccurredAt: r.OccurredAt,
            IngestedAt: r.IngestedAt,
            IdempotencyKey: r.IdempotencyKey,
            PrevEventHash: r.PrevEventHash);
    }
}
