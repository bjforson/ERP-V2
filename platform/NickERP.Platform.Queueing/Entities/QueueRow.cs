using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Queueing.Entities;

/// <summary>
/// The uniform row shape for every Postgres-backed work queue. One physical
/// table per logical queue (e.g. <c>inspection.queue_split_detection</c>,
/// <c>inspection.queue_audit_review</c>) — same columns, different table
/// names, different consumers. Tables are created via EF migrations in the
/// owning module's <c>*.Database</c> project; the CLR shape is shared from
/// here so the platform's <c>PostgresQueue&lt;TPayload&gt;</c> implementation
/// can speak to all of them uniformly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim semantics.</b> Workers claim with
/// <c>FOR UPDATE SKIP LOCKED</c> on the partial index
/// <c>("AvailableAt") WHERE "ClaimedBy" IS NULL</c>. Multiple workers can
/// pull concurrently with zero contention; the canonical reference for
/// scale is GitLab's job-queue, which runs the same pattern on Postgres
/// past 10M rows/day.
/// </para>
/// <para>
/// <b>Lease.</b> A claim sets <see cref="ClaimedBy"/> + <see cref="ClaimedUntil"/>
/// (typically <c>now() + 5 min</c>). If the worker dies mid-process,
/// <c>QueueJanitor</c> finds rows where <see cref="ClaimedUntil"/> is in
/// the past and resets <see cref="ClaimedBy"/> to NULL — a different
/// worker picks the row up on the next claim. The <see cref="AttemptCount"/>
/// increments on each claim so a poison message eventually lands in the
/// dead-letter queue (configurable; default 5 attempts).
/// </para>
/// <para>
/// <b>Idempotency.</b> <see cref="IdempotencyKey"/> is UNIQUE per queue.
/// Duplicate INSERTs (from a producer retry) collide on the unique
/// constraint and the producer treats the conflict as a no-op. Construct
/// the key via <see cref="NickERP.Platform.Audit.IdempotencyKey.From"/>
/// using a stable input set
/// (<c>workItemId | trigger | toState | createdAt-rounded-to-second</c>).
/// </para>
/// <para>
/// <b>Tenancy.</b> Implements <see cref="ITenantOwned"/> so the
/// <c>TenantOwnedEntityInterceptor</c> stamps <see cref="TenantId"/> on
/// insert. Every queue table also gets the standard
/// <c>tenant_isolation_*</c> RLS policy in its module's RLS migration.
/// A worker running in a different tenant's request scope can never see
/// or claim another tenant's rows even if it forgets a WHERE clause.
/// </para>
/// </remarks>
public class QueueRow : ITenantOwned
{
    /// <summary>Stable primary key — <c>bigserial</c> so high-volume queues don't run out.</summary>
    public long Id { get; set; }

    /// <inheritdoc />
    public long TenantId { get; set; }

    /// <summary>FK to the <see cref="WorkItem{TState}"/> this row dispatches work for.</summary>
    public Guid WorkItemId { get; set; }

    /// <summary>Wallclock the row was inserted. DB default <c>now()</c>.</summary>
    public DateTimeOffset EnqueuedAt { get; set; }

    /// <summary>
    /// Earliest moment a worker may claim this row. Equals
    /// <see cref="EnqueuedAt"/> for ready-now work; set further in the
    /// future for scheduled retries or backoff.
    /// </summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>
    /// Worker identity that currently holds the lease, or <c>null</c> if
    /// the row is unclaimed. Convention: <c>{machine-name}/{pid}/{worker-name}</c>
    /// so ops can correlate a stuck claim back to a process.
    /// </summary>
    public string? ClaimedBy { get; set; }

    /// <summary>
    /// Lease expiry. While <c>now() &lt;= ClaimedUntil</c>, the lease is
    /// valid; afterwards <c>QueueJanitor</c> may release it. Workers that
    /// expect to take longer than the default lease should renew via
    /// <see cref="Abstractions.IQueueClaim{TPayload}.RenewLeaseAsync"/>.
    /// </summary>
    public DateTimeOffset? ClaimedUntil { get; set; }

    /// <summary>
    /// Number of times this row has been claimed. Increments on every
    /// claim — including reclaims after a lease expires. When this
    /// crosses the configured maximum (default 5), the row is moved to
    /// the dead-letter queue with <see cref="LastError"/> populated.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Consumer-specific JSON blob. Stored as <c>jsonb</c>. Deserialised
    /// into the queue's <c>TPayload</c> generic by
    /// <c>PostgresQueue&lt;TPayload&gt;.ClaimAsync</c>. Default is the
    /// empty object so a missing payload doesn't trip Npgsql's
    /// <c>NotNull</c> constraint.
    /// </summary>
    public string Payload { get; set; } = "{}";

    /// <summary>
    /// Deterministic dedup key. UNIQUE per queue table. Producers
    /// retrying after a transient failure produce the same key; the
    /// duplicate INSERT collides on the unique index and the producer
    /// treats it as a no-op.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Last failure message captured by a consumer. Cleared on
    /// successful processing (the row is deleted). Populated when a
    /// consumer throws and the row is requeued or moved to DLQ.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Optional correlation id from the producing request — same
    /// semantics as <see cref="WorkItemTransition.CorrelationId"/>.
    /// Propagated end-to-end so tracing survives the queue handoff.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>UTC creation timestamp. DB default <c>now()</c>; immutable.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
