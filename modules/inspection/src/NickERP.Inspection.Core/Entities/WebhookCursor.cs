using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Sprint 41 / Phase C — per-tenant per-adapter cursor for the outbound
/// webhook dispatcher. One row per (TenantId, AdapterName); the
/// dispatcher records the highest <c>audit.events.event_id</c> it has
/// successfully forwarded to the adapter, and reads forward from there
/// on the next tick.
///
/// <para>
/// <b>Why per-adapter, not per-tenant?</b> Two webhook adapters wired
/// for the same tenant (e.g. an internal SIEM forwarder + an external
/// risk-scoring partner) advance independently — if the SIEM is down
/// for an hour, the risk-scoring partner shouldn't replay an hour's
/// worth of events when the SIEM comes back. The compound key keeps
/// each adapter's cursor isolated.
/// </para>
///
/// <para>
/// <b>Cursor semantics.</b> <see cref="LastProcessedEventId"/> is the
/// Guid PK of the audit event most recently dispatched. The dispatcher
/// orders <c>audit.events</c> by <c>ingested_at, event_id</c> (stable
/// fallback so two events with the same wall-clock land in deterministic
/// order) and pulls all rows after the cursor. Idempotency on the
/// adapter side is per-event via
/// <see cref="NickERP.Inspection.Webhooks.Abstractions.WebhookEvent.IdempotencyKey"/>.
/// </para>
///
/// <para>
/// <b>No DELETE.</b> Cursors persist for the life of an adapter
/// configuration; if an adapter is removed, the row stays so re-adding
/// the adapter resumes from where it left off rather than re-dispatching
/// the entire backlog. Operators wishing to force a replay can update
/// <see cref="LastProcessedEventId"/> to <see cref="Guid.Empty"/> via a
/// dedicated admin endpoint (out of scope for this sprint).
/// </para>
/// </summary>
public sealed class WebhookCursor : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Adapter name — the adapter implementation's <c>AdapterName</c> property.</summary>
    public string AdapterName { get; set; } = string.Empty;

    /// <summary>
    /// EventId of the last audit event the adapter successfully accepted.
    /// <see cref="Guid.Empty"/> on the first tick — the dispatcher reads
    /// from the start of the audit stream when this is the sentinel.
    /// </summary>
    public Guid LastProcessedEventId { get; set; }

    /// <summary>When the cursor was last advanced.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public long TenantId { get; set; }
}
