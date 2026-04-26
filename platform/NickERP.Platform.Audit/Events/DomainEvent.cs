using System.Text.Json;

namespace NickERP.Platform.Audit.Events;

/// <summary>
/// The canonical shape of every state-change record in the suite. One row
/// per emitted event, persisted in the append-only <c>audit.events</c>
/// table in <c>nickerp_platform</c> and dispatched (eventually) via the
/// Postgres LISTEN/NOTIFY bus to in-process subscribers.
/// </summary>
/// <param name="EventId">Server-assigned unique id (Guid). Set by the persistence layer on insert; <see cref="Guid.Empty"/> on construction.</param>
/// <param name="TenantId">The tenant the event belongs to.</param>
/// <param name="ActorUserId">Who caused the event — a canonical <see cref="Guid"/> from the Identity layer (user or service-token id). Nullable for system-emitted events.</param>
/// <param name="CorrelationId">Cross-service request correlation. Should match the structured-log <c>CorrelationId</c> for the same request.</param>
/// <param name="EventType">Stable dotted code (e.g. <c>nickerp.identity.user_created</c>, <c>nickerp.inspection.case_reviewed</c>). Lowercase, dot-segmented, kept stable for the life of consumers.</param>
/// <param name="EntityType">Domain type the event is about (<c>IdentityUser</c>, <c>InspectionCase</c>). Free-form for indexing.</param>
/// <param name="EntityId">Primary key of the affected entity, serialised as string.</param>
/// <param name="Payload">JSON payload — usually the relevant fields of the changed entity, or the diff. Schema is per <see cref="EventType"/>; consumers MUST be tolerant of unknown fields.</param>
/// <param name="OccurredAt">When the underlying state change happened (set by the publishing service, not by the audit infrastructure).</param>
/// <param name="IngestedAt">When the audit layer persisted the row. May lag <see cref="OccurredAt"/> if the outbox queues events for async publication.</param>
/// <param name="IdempotencyKey">Deterministic key used to dedupe replays. Two events with the same key are treated as the same event; the second insert is silently skipped.</param>
/// <param name="PrevEventHash">Optional tamper-evident chain — sha256 of the previous event for the same <paramref name="EntityId"/>. Reserved for compliance-grade audit; populated by the persistence layer when enabled.</param>
public sealed record DomainEvent(
    Guid EventId,
    long TenantId,
    Guid? ActorUserId,
    string? CorrelationId,
    string EventType,
    string EntityType,
    string EntityId,
    JsonElement Payload,
    DateTimeOffset OccurredAt,
    DateTimeOffset IngestedAt,
    string IdempotencyKey,
    string? PrevEventHash)
{
    /// <summary>Convenience factory for emitting a new event from application code. EventId is left empty; the persistence layer fills it on insert.</summary>
    public static DomainEvent Create(
        long tenantId,
        Guid? actorUserId,
        string? correlationId,
        string eventType,
        string entityType,
        string entityId,
        JsonElement payload,
        string idempotencyKey,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return new DomainEvent(
            EventId: Guid.Empty,
            TenantId: tenantId,
            ActorUserId: actorUserId,
            CorrelationId: correlationId,
            EventType: eventType,
            EntityType: entityType,
            EntityId: entityId,
            Payload: payload,
            OccurredAt: now,
            IngestedAt: now,
            IdempotencyKey: idempotencyKey,
            PrevEventHash: null);
    }
}
