namespace NickERP.Platform.Audit.Events;

/// <summary>
/// Module-facing API for emitting <see cref="DomainEvent"/>s. Modules call
/// this from application services after a state change. The implementation
/// (in <c>NickERP.Platform.Audit.Database</c>) handles persistence,
/// idempotency, and routing to the LISTEN/NOTIFY bus.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Persist the event to <c>audit.events</c> and post a NOTIFY for any
    /// subscribers. If the same <see cref="DomainEvent.IdempotencyKey"/>
    /// has already been recorded for the same tenant, this is a no-op
    /// (the existing event is returned with its <see cref="DomainEvent.EventId"/> filled).
    /// </summary>
    /// <returns>The persisted event with its <see cref="DomainEvent.EventId"/> populated.</returns>
    Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Persist a batch of events as a single transaction. Useful for command
    /// handlers that emit multiple events from one operation. Idempotency
    /// dedup is per-event.
    /// </summary>
    Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default);
}

/// <summary>
/// In-process event bus. Subscribers receive a callback for every event
/// emitted on a channel they're subscribed to. Implementations may use
/// Postgres LISTEN/NOTIFY for cross-process delivery; the contract here
/// is in-process semantics only.
/// </summary>
public interface IEventBus
{
    /// <summary>Publish an event onto a named channel. Most callers should use <see cref="IEventPublisher"/> instead, which records to audit AND publishes here.</summary>
    Task PublishAsync(string channel, DomainEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Subscribe to a channel. The handler is invoked once per delivered
    /// event; throw to abandon (the bus does NOT auto-retry — handlers must
    /// be idempotent and use <see cref="DomainEvent.IdempotencyKey"/> for
    /// dedup if they care).
    /// </summary>
    /// <returns>Disposable subscription handle. Disposing detaches the handler.</returns>
    IAsyncDisposable Subscribe(string channel, Func<DomainEvent, CancellationToken, Task> handler);
}
