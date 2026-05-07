namespace NickERP.Platform.Queueing.Entities;

/// <summary>
/// Row promoted from a regular queue (or the outbox) after exceeding the
/// retry budget. The dead-letter queue is a single shared table —
/// <c>queueing.dead_letter</c> — fed by every queue in the platform.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why dead-letter rather than delete.</b> A poison message in a live
/// system is operational signal: it tells you a deploy regressed a
/// consumer, or a downstream contract changed, or a payload edge case
/// wasn't covered. Throwing it away loses that signal; capturing it
/// here gives ops a manual surface to inspect, optionally retry-with-reset,
/// or abandon with a recorded reason.
/// </para>
/// <para>
/// <b>Originating context preserved.</b> <see cref="SourceQueue"/> +
/// <see cref="SourceQueueRowId"/> tell ops which queue the row failed in;
/// the inherited <see cref="QueueRow.Payload"/> + <see cref="QueueRow.LastError"/>
/// tell them what was being processed and why it broke.
/// <see cref="QueueRow.AttemptCount"/> records how many attempts were made
/// before the row was promoted.
/// </para>
/// <para>
/// <b>Retry path.</b> Ops can re-enqueue a DLQ row by calling
/// <c>IDeadLetterQueue.RetryAsync(rowId)</c> which resets
/// <see cref="QueueRow.AttemptCount"/> to zero, copies the payload back
/// into <see cref="SourceQueue"/>, and deletes the DLQ row. Or abandon
/// via <c>AbandonAsync(rowId, reason)</c> which deletes after writing a
/// <c>WorkItemTransition</c> with <c>Trigger="DlqAbandon"</c> so the
/// abandonment is auditable.
/// </para>
/// </remarks>
public sealed class QueueDeadLetterRow : QueueRow
{
    /// <summary>
    /// Logical name of the queue the row originated from. Convention is
    /// <c>"{schema}.{table}"</c> — e.g. <c>"inspection.queue_split_detection"</c> —
    /// so ops can union DLQ rows with the live queue without ambiguity.
    /// </summary>
    public string SourceQueue { get; set; } = string.Empty;

    /// <summary>
    /// The <see cref="QueueRow.Id"/> of the row in <see cref="SourceQueue"/>
    /// at the moment it was promoted. The original row is deleted on
    /// promotion (so the live queue is small) — this id is kept for
    /// audit cross-reference, not for live joining.
    /// </summary>
    public long SourceQueueRowId { get; set; }

    /// <summary>UTC moment the row was promoted from <see cref="SourceQueue"/> to DLQ.</summary>
    public DateTimeOffset PromotedAt { get; set; }
}
