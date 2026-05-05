namespace NickERP.Inspection.Webhooks.Abstractions;

/// <summary>
/// Sprint 47 / Phase A — wire shape of one outbound webhook event.
/// The dispatcher builds this record from a corresponding
/// <c>audit.events</c> row whose <c>EventType</c> matches
/// <see cref="WebhookEventTypes"/>.
///
/// <para>
/// <b>Vendor-neutral.</b> Adapters that need authority-shaped fields
/// pull them out of <see cref="Payload"/> by key — the record itself
/// stays free of country / vendor concepts.
/// </para>
///
/// <para>
/// <b>Idempotency.</b> <see cref="IdempotencyKey"/> is the audit
/// event's <c>EventId</c> Guid — same value across retries so
/// downstream subscribers can dedupe reliably. The dispatcher's
/// per-(TenantId, AdapterName) cursor narrows the read window
/// further; <see cref="IdempotencyKey"/> is the within-batch dedup
/// key.
/// </para>
/// </summary>
/// <param name="EventType">
/// Stable code from <see cref="WebhookEventTypes"/> (e.g.
/// <c>HIGH_RISK_SCAN_DETECTED</c>). Adapters that subscribe to a
/// subset filter on this constant.
/// </param>
/// <param name="TenantId">Owning tenant. The dispatcher fans out per-tenant.</param>
/// <param name="EntityId">
/// Primary key of the affected entity, when applicable. Nullable
/// because some standard events (e.g. <c>AI_MODEL_DRIFT_ALERT</c>)
/// don't have a single entity instance.
/// </param>
/// <param name="EntityType">
/// Domain type the event is about (<c>InspectionCase</c>,
/// <c>ScannerDeviceInstance</c>, <c>ScanArtifact</c>, ...). Free-form
/// for indexing.
/// </param>
/// <param name="Payload">
/// Per-event-type body. Schema is per <see cref="EventType"/>;
/// adapters MUST be tolerant of unknown keys so additive payload
/// changes don't break subscribers.
/// </param>
/// <param name="OccurredAt">
/// When the underlying state change happened (set by the publisher,
/// not by the dispatcher).
/// </param>
/// <param name="IdempotencyKey">
/// The audit event's server-assigned <see cref="System.Guid"/> id.
/// Same value across dispatcher retries; downstream subscribers
/// dedupe on this.
/// </param>
public sealed record WebhookEvent(
    string EventType,
    long TenantId,
    Guid? EntityId,
    string EntityType,
    IReadOnlyDictionary<string, object> Payload,
    DateTimeOffset OccurredAt,
    Guid IdempotencyKey);
