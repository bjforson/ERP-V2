namespace NickERP.Platform.Queueing.Abstractions;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Producer + consumer surface for a single named work queue. One concrete
/// queue per <typeparamref name="TPayload"/> per logical channel —
/// <c>IQueue&lt;SplitDetectionPayload&gt;</c> bound to
/// <c>inspection.queue_split_detection</c>, etc.
/// </summary>
/// <typeparam name="TPayload">
/// Per-row consumer-specific data carried alongside the work item id.
/// Serialised to <c>jsonb</c> in <see cref="Entities.QueueRow.Payload"/>.
/// Use a record class for clarity; nothing in the platform depends on it
/// being mutable.
/// </typeparam>
public interface IQueue<TPayload>
{
    /// <summary>
    /// Logical name of the queue (matches the suffix of the physical
    /// table — e.g. <c>"split_detection"</c> binds to
    /// <c>inspection.queue_split_detection</c>). Used as the LISTEN
    /// channel and as the <see cref="Entities.QueueDeadLetterRow.SourceQueue"/>
    /// label on promotion.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Postgres LISTEN/NOTIFY channel used for low-latency wakeups.
    /// Includes schema and queue name to avoid collisions between modules.
    /// </summary>
    string NotifyChannel { get; }

    /// <summary>
    /// Insert a row. Returns the row's primary key. Idempotency is
    /// enforced via the unique constraint on
    /// <see cref="Entities.QueueRow.IdempotencyKey"/>: a duplicate INSERT
    /// is treated as a no-op and the existing row's id is returned.
    /// </summary>
    /// <remarks>
    /// State-machine producers should prefer
    /// <see cref="ITransactionalQueue{TPayload}"/> so the state change
    /// and queue insert commit atomically. This standalone path is for
    /// tests and explicitly tenant-scoped producers.
    /// </remarks>
    Task<long> EnqueueAsync(EnqueueRequest<TPayload> request, CancellationToken ct = default);

    /// <summary>
    /// Atomically pick the next available row, mark it claimed, and
    /// return a handle the consumer can use to complete / fail / renew.
    /// Returns <c>null</c> if no row is available.
    /// </summary>
    /// <param name="workerId">
    /// Identity of the claiming worker (convention:
    /// <c>{machine}/{pid}/{worker-name}</c>). Stamped onto
    /// <see cref="Entities.QueueRow.ClaimedBy"/> so ops can correlate
    /// stuck claims to a process.
    /// </param>
    /// <param name="leaseDuration">
    /// How long the worker has before the janitor may reclaim the row.
    /// Pass a value &gt;= the worst-case processing time the worker
    /// expects; renew via the returned handle if the work might run
    /// long.
    /// </param>
    /// <param name="ct">Propagates cancellation from the caller (typically the consumer host's stopping token).</param>
    /// <returns>
    /// A claim handle, or <c>null</c> if the queue is empty / all rows
    /// are claimed / unavailable. Dispose the handle to release the
    /// lease (auto-fails if neither Complete nor Fail was called first).
    /// </returns>
    Task<IQueueClaim<TPayload>?> ClaimAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    /// <summary>
    /// Quick depth probe — count of unclaimed rows whose
    /// <see cref="Entities.QueueRow.AvailableAt"/> has passed. Cheap
    /// (uses the partial index); safe to call frequently from the
    /// observability layer.
    /// </summary>
    Task<long> GetReadyDepthAsync(CancellationToken ct = default);
}

/// <summary>
/// Producer-only surface for inserting queue rows through an existing EF
/// Core transaction. State-machine producers use this path so their state
/// update, transition audit row, and downstream queue row commit or roll
/// back as one database unit.
/// </summary>
/// <typeparam name="TPayload">Payload type carried in the queue row.</typeparam>
public interface ITransactionalQueue<TPayload>
{
    /// <summary>
    /// Insert a row using <paramref name="db"/>'s current relational
    /// transaction. Throws if no transaction is active, because falling
    /// back to an independent connection would break producer atomicity.
    /// </summary>
    Task<long> EnqueueAsync(
        DbContext db,
        EnqueueRequest<TPayload> request,
        CancellationToken ct = default);
}

/// <summary>
/// Producer-side input to <see cref="IQueue{TPayload}.EnqueueAsync"/>.
/// </summary>
/// <typeparam name="TPayload">Payload type carried in the queue row.</typeparam>
public sealed class EnqueueRequest<TPayload>
{
    /// <summary>The work item the row dispatches work for. Required.</summary>
    public required Guid WorkItemId { get; init; }

    /// <summary>The consumer-specific data for this row. Required.</summary>
    public required TPayload Payload { get; init; }

    /// <summary>
    /// Deterministic dedup key — see <see cref="Entities.QueueRow.IdempotencyKey"/>.
    /// Build via <see cref="NickERP.Platform.Audit.IdempotencyKey.From"/> with a stable parts list.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>
    /// Optional scheduling — pass a future <c>DateTimeOffset</c> to make
    /// the row available later (delayed retries, scheduled work).
    /// Defaults to <c>now()</c> on the DB side when null.
    /// </summary>
    public DateTimeOffset? AvailableAt { get; init; }

    /// <summary>
    /// Optional correlation id — propagated to
    /// <see cref="Entities.QueueRow.CorrelationId"/> so ops can trace
    /// the queue handoff back to the producing request.
    /// </summary>
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Lease handle returned by <see cref="IQueue{TPayload}.ClaimAsync"/>. The
/// consumer either calls <see cref="CompleteAsync"/> on success or
/// <see cref="FailAsync"/> on failure; if it does neither and the handle
/// is disposed, the row's lease is left to expire and the janitor
/// reclaims it (which counts as an attempt).
/// </summary>
/// <typeparam name="TPayload">Payload type carried in the row.</typeparam>
public interface IQueueClaim<TPayload> : IAsyncDisposable
{
    /// <summary>The claimed row's primary key.</summary>
    long Id { get; }

    /// <summary>The work item this row dispatches work for.</summary>
    Guid WorkItemId { get; }

    /// <summary>Tenant that owns the row. RLS-enforced; consumers can rely on this matching their request scope.</summary>
    long TenantId { get; }

    /// <summary>Number of times this row has been claimed (including the current claim).</summary>
    int AttemptCount { get; }

    /// <summary>The deserialised payload.</summary>
    TPayload Payload { get; }

    /// <summary>Wallclock until which the lease is valid; renew via <see cref="RenewLeaseAsync"/> if processing might run long.</summary>
    DateTimeOffset ClaimedUntil { get; }

    /// <summary>Optional correlation id propagated from the producer.</summary>
    string? CorrelationId { get; }

    /// <summary>
    /// Mark the row processed and delete it. Idempotent — calling twice
    /// is a no-op (the second call observes the row missing).
    /// </summary>
    Task CompleteAsync(CancellationToken ct = default);

    /// <summary>
    /// Mark the row failed. If <see cref="Entities.QueueRow.AttemptCount"/>
    /// has not exceeded the queue's retry budget, the row is requeued
    /// with <see cref="Entities.QueueRow.AvailableAt"/> shifted by
    /// <paramref name="retryAfter"/> (or computed exponential backoff if
    /// null) and <see cref="Entities.QueueRow.LastError"/> set to
    /// <paramref name="error"/>'s message. If the budget is exhausted,
    /// the row is moved to the dead-letter queue.
    /// </summary>
    Task FailAsync(Exception error, TimeSpan? retryAfter = null, CancellationToken ct = default);

    /// <summary>
    /// Extend the lease by <paramref name="additional"/>. Useful when a
    /// consumer realises mid-process that the work will run past the
    /// initial lease window; calling this before <see cref="ClaimedUntil"/>
    /// expires keeps the row out of the janitor's hands.
    /// </summary>
    Task RenewLeaseAsync(TimeSpan additional, CancellationToken ct = default);
}
