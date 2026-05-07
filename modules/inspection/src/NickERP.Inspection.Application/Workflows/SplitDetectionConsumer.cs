using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Sprint 14 / B-queues — proof-of-life consumer for the
/// <c>inspection.queue_split_detection</c> queue. Implements
/// <see cref="IQueueConsumer{TPayload}"/> for
/// <see cref="SplitDetectionPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is right now.</b> A placeholder body that logs the
/// claimed row's payload and returns successfully. The point of this
/// class in Sprint 14 is the wiring — registering a real
/// <see cref="IQueueConsumer{TPayload}"/> against the platform queueing
/// runtime so the LISTEN/poll fallback, claim management, and DLQ
/// promotion can be exercised end-to-end against a live Postgres without
/// shipping the actual splitter integration in the same sprint.
/// </para>
/// <para>
/// <b>What lands later.</b> The actual call into the imaging pipeline
/// (today the v1 Python image-splitter that the
/// <c>NickERP.Inspection.Imaging</c> assembly wraps) is intentionally
/// deferred — the splitter integration carries its own design questions
/// (subprocess vs. in-proc, vendor adapter routing, SplitStatus state
/// transitions) that aren't blocked by this scaffold. When that lands,
/// this consumer's body becomes a thin dispatch into the imaging
/// adapter, the placeholder log call disappears, and the failure modes
/// (timeout, splitter crash, output validation) flow naturally through
/// the existing <see cref="IQueueClaim{TPayload}.FailAsync"/> retry +
/// DLQ machinery.
/// </para>
/// <para>
/// <b>Idempotency contract.</b> Honouring the
/// <see cref="IQueueConsumer{TPayload}"/> contract: the placeholder body
/// is naturally idempotent (logging only). When the real splitter call
/// lands, it MUST stay idempotent under retry — janitor reclaims, lease
/// expiries, and worker restarts all replay the same claim, and the
/// queueing layer guarantees at-least-once not exactly-once delivery.
/// </para>
/// <para>
/// <b>Throw to fail.</b> The host wraps the call and routes thrown
/// exceptions to <see cref="IQueueClaim{TPayload}.FailAsync"/>; consumers
/// don't call Fail directly. The placeholder body never throws, so
/// every claim auto-completes — that's the proof-of-life signal.
/// </para>
/// </remarks>
public sealed class SplitDetectionConsumer : IQueueConsumer<SplitDetectionPayload>
{
    private readonly ILogger<SplitDetectionConsumer> _logger;

    /// <summary>
    /// Construct the consumer. Registered as scoped — the platform host
    /// resolves a fresh instance per claimed row, so capturing the
    /// per-row claim in instance state would be unsafe but isn't done
    /// here.
    /// </summary>
    /// <param name="logger">DI-supplied logger; used for the proof-of-life trace.</param>
    public SplitDetectionConsumer(ILogger<SplitDetectionConsumer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task ProcessAsync(IQueueClaim<SplitDetectionPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // Sprint 14 proof-of-life: log + return. The structured fields
        // mirror what the real consumer will likely emit on completion
        // (CaseId for correlation, ImageRef for trace-reconstruction,
        // AttemptCount so reclaim-loop diagnoses are spot-checkable from
        // the log alone).
        _logger.LogInformation(
            "Sprint 14 split-detection placeholder ran for CaseId={CaseId} ImageRef={ImageRef} AttemptCount={AttemptCount} (no-op — real splitter integration lands in a later sprint)",
            claim.Payload.CaseId,
            claim.Payload.ImageRef,
            claim.AttemptCount);

        return Task.CompletedTask;
    }
}
