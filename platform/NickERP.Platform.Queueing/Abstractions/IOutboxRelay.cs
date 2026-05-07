using NickERP.Platform.Queueing.Entities;

namespace NickERP.Platform.Queueing.Abstractions;

/// <summary>
/// Adapter contract for outbound integrations served by the
/// <c>queueing.outbox</c> table. Implementations register themselves in
/// DI by destination kind:
/// <code>
/// services.AddSingleton&lt;IOutboundAdapter, IcumsOutboundAdapter&gt;();
/// // adapter's DestinationKind = "icums"
/// </code>
/// The platform's <c>OutboxRelay</c> looks up the adapter by
/// <see cref="DestinationKind"/> at dispatch time.
/// </summary>
public interface IOutboundAdapter
{
    /// <summary>
    /// Identifier matched against
    /// <see cref="QueueOutboxRow.DestinationKind"/>. Use stable
    /// kebab-cased strings: <c>"icums"</c>,
    /// <c>"customs-authority"</c>, <c>"comms-gateway"</c>,
    /// <c>"webhook"</c>. Must be unique per adapter.
    /// </summary>
    string DestinationKind { get; }

    /// <summary>
    /// Deliver one outbox row to the external system. Return normally
    /// on success — the relay deletes the row. Throw to fail; the relay
    /// captures the exception, lets the lease expire, and the row gets
    /// retried with exponential backoff.
    /// </summary>
    /// <remarks>
    /// <b>Idempotency at the destination.</b> Adapters MUST tolerate
    /// retries. The platform guarantees the same outbox row will be
    /// dispatched if a previous attempt failed; the adapter is
    /// responsible for ensuring the receiving system either accepts the
    /// retry or de-dupes on its own key (typical pattern: pass
    /// <see cref="QueueRow.IdempotencyKey"/> in a header / body field
    /// the destination knows to check).
    /// </remarks>
    Task DispatchAsync(QueueOutboxRow row, CancellationToken ct);
}

/// <summary>
/// Convenience surface for producing outbox rows from inside a
/// state-machine transition. The state machine writes the work-item
/// state change AND the outbox row in a single transaction so the
/// outbound message is durable regardless of whether the external call
/// is reachable.
/// </summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Insert an outbox row. Caller is expected to be inside a DB
    /// transaction (typically the state-machine transition's). Idempotency
    /// is enforced by the unique key on
    /// <see cref="QueueRow.IdempotencyKey"/>.
    /// </summary>
    Task<long> WriteAsync(OutboxWriteRequest request, CancellationToken ct = default);
}

/// <summary>Producer-side input to <see cref="IOutboxWriter.WriteAsync"/>.</summary>
public sealed class OutboxWriteRequest
{
    /// <summary>The work item the outbound message belongs to.</summary>
    public required Guid WorkItemId { get; init; }

    /// <summary>Adapter routing key — see <see cref="IOutboundAdapter.DestinationKind"/>.</summary>
    public required string DestinationKind { get; init; }

    /// <summary>
    /// Concrete destination identifier within the kind (e.g. ICUMS
    /// declaration number, webhook subscription id). Carried so the
    /// adapter doesn't need to round-trip back to the work item.
    /// </summary>
    public required string DestinationName { get; init; }

    /// <summary>HTTP-style content type for <see cref="Body"/>.</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>The serialised body the adapter will deliver. Stored in <see cref="QueueRow.Payload"/>.</summary>
    public required string Body { get; init; }

    /// <summary>Deterministic idempotency key — see <see cref="EnqueueRequest{TPayload}.IdempotencyKey"/>.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Optional correlation id propagated end-to-end.</summary>
    public string? CorrelationId { get; init; }
}
