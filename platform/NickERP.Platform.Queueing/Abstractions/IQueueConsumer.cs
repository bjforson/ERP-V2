namespace NickERP.Platform.Queueing.Abstractions;

/// <summary>
/// Module-facing handler for a single queue. One implementation per
/// <typeparamref name="TPayload"/>; registered in DI via
/// <c>services.AddQueueConsumer&lt;MySplitDetectionConsumer, SplitDetectionPayload&gt;("split_detection")</c>.
/// The platform's <c>QueueConsumerHost&lt;TPayload&gt;</c> wraps the
/// consumer in a <c>BackgroundService</c> that LISTENs for wakeups,
/// claims rows, calls <see cref="ProcessAsync"/>, and handles complete /
/// fail bookkeeping.
/// </summary>
/// <typeparam name="TPayload">Payload type carried in the queue rows this consumer drains.</typeparam>
/// <remarks>
/// <para>
/// <b>Idempotency contract.</b> Consumers MUST be safe to call more
/// than once for the same row (lease can expire mid-process; janitor
/// reclaims; a different worker re-runs the work). Use the
/// <see cref="IQueueClaim{TPayload}.WorkItemId"/> +
/// <see cref="IQueueClaim{TPayload}.AttemptCount"/> as inputs to a
/// downstream idempotency check if the work has externally visible
/// side-effects.
/// </para>
/// <para>
/// <b>Throw to fail.</b> The host catches exceptions and calls
/// <see cref="IQueueClaim{TPayload}.FailAsync"/> on the claim with the
/// thrown exception. Consumers don't need to call Fail themselves; just
/// throw and the row is requeued (or dead-lettered if the budget is
/// exhausted).
/// </para>
/// <para>
/// <b>Don't call Complete inside ProcessAsync.</b> The host calls it
/// after the method returns successfully. Calling it inside leaks the
/// abstraction and breaks the host's bookkeeping.
/// </para>
/// </remarks>
public interface IQueueConsumer<TPayload>
{
    /// <summary>
    /// Process one claimed row. Return normally on success; throw on
    /// failure. The host wraps the call in claim-management bookkeeping.
    /// </summary>
    /// <param name="claim">
    /// The claimed row. <see cref="IQueueClaim{TPayload}.RenewLeaseAsync"/>
    /// is the one method consumers may legitimately call mid-process —
    /// e.g. when a long-running OCR pass is half done and needs more
    /// time before the janitor reclaims.
    /// </param>
    /// <param name="ct">
    /// Cancellation propagated from the host's
    /// <c>StoppingToken</c>. Honour it for graceful shutdown.
    /// </param>
    Task ProcessAsync(IQueueClaim<TPayload> claim, CancellationToken ct);
}
