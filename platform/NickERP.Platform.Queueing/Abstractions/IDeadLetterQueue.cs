using NickERP.Platform.Queueing.Entities;

namespace NickERP.Platform.Queueing.Abstractions;

/// <summary>
/// Operator-facing surface for the dead-letter queue. The DLQ is
/// platform-wide (<c>queueing.dead_letter</c>); rows arrive from every
/// queue once their retry budget is exhausted. Ops drives this from the
/// <c>/admin/queues/dlq</c> portal page.
/// </summary>
public interface IDeadLetterQueue
{
    /// <summary>
    /// Enumerate DLQ rows for a tenant, optionally scoped to a single
    /// source queue. Newest-first; paginated. Tenant scope is enforced
    /// by RLS — passing a tenant id from a request scope that doesn't
    /// own it returns an empty page rather than throwing.
    /// </summary>
    Task<IReadOnlyList<QueueDeadLetterRow>> ListAsync(
        long tenantId,
        string? sourceQueueFilter,
        int pageSize,
        long? afterId,
        CancellationToken ct = default);

    /// <summary>
    /// Re-enqueue a DLQ row. Resets attempt count to zero, copies the
    /// payload back into <see cref="QueueDeadLetterRow.SourceQueue"/>,
    /// deletes the DLQ row. Writes a <c>WorkItemTransition</c> with
    /// <c>Trigger="DlqRetry"</c> and <c>Actor=&lt;operator&gt;</c> so
    /// the retry is auditable.
    /// </summary>
    Task RetryAsync(long deadLetterRowId, string actor, string reason, CancellationToken ct = default);

    /// <summary>
    /// Permanently abandon a DLQ row. Deletes the row after writing a
    /// <c>WorkItemTransition</c> with <c>Trigger="DlqAbandon"</c>.
    /// Reason is required (no silent abandonment).
    /// </summary>
    Task AbandonAsync(long deadLetterRowId, string actor, string reason, CancellationToken ct = default);
}
