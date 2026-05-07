namespace NickERP.Platform.Queueing.Entities;

/// <summary>
/// Cross-system message awaiting delivery to an external endpoint
/// (ICUMS, customs authority webhooks, NickComms gateway, etc.). Inserted
/// in the same DB transaction as the state-machine transition that
/// produced it so the message is durable regardless of whether the
/// external call succeeds; <see cref="DestinationKind"/> + <see cref="DestinationName"/>
/// route it to the right adapter at relay time.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>queueing.outbox</c> (shared platform schema). One physical
/// table for the whole platform; <see cref="DestinationKind"/> identifies
/// which adapter pipeline picks it up. The relay
/// (<c>OutboxRelay</c> in <c>NickERP.Platform.Queueing</c>) reads claimed
/// rows, looks up the matching <c>IOutboundAdapter</c> by
/// <see cref="DestinationKind"/>, and dispatches.
/// </para>
/// <para>
/// <b>Why a single shared outbox.</b> Modules generalising
/// <c>OutboundSubmissionDispatchWorker</c> in v2 inspection prove the
/// pattern works for one module; centralising it lets finance / comms /
/// inspection share one relay process and one set of operational
/// dashboards. Per-destination concerns (rate limiting, circuit
/// breakers, signing) live in the adapter, not in this row.
/// </para>
/// <para>
/// <b>Reliability story.</b> The relay drains the outbox via the same
/// <c>FOR UPDATE SKIP LOCKED</c> claim semantics as any other queue. On
/// adapter success the row is deleted. On adapter failure the lease
/// expires and the row gets retried with exponential backoff via
/// <see cref="QueueRow.AvailableAt"/>; after N failures it lands in the
/// dead-letter queue with <see cref="QueueRow.LastError"/> capturing the
/// final exception.
/// </para>
/// </remarks>
public sealed class QueueOutboxRow : QueueRow
{
    /// <summary>
    /// Adapter kind identifier. Examples: <c>"icums"</c>,
    /// <c>"customs-authority"</c>, <c>"comms-gateway"</c>,
    /// <c>"webhook"</c>. The relay looks up
    /// <c>IOutboundAdapter</c> implementations keyed on this string.
    /// </summary>
    public string DestinationKind { get; set; } = string.Empty;

    /// <summary>
    /// Concrete destination identifier within the kind — e.g. the ICUMS
    /// declaration number, the webhook subscription id. Carried so the
    /// adapter doesn't need to round-trip back to the work item to
    /// figure out where to send.
    /// </summary>
    public string DestinationName { get; set; } = string.Empty;

    /// <summary>
    /// HTTP-style content type of the payload (e.g.
    /// <c>"application/json"</c>, <c>"application/xml"</c>,
    /// <c>"application/x-customs-cmr"</c>). Adapter-specific; the relay
    /// passes it through unchanged.
    /// </summary>
    public string ContentType { get; set; } = "application/json";
}
