using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Sprint S+3 / B-queues — placeholder consumer for the
/// <c>inspection.queue_image_analysis</c> queue. Implements
/// <see cref="IQueueConsumer{TPayload}"/> for
/// <see cref="ImageAnalysisPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is right now.</b> A compile-clean stub that logs a
/// single TODO once on first claim then completes successfully. Lays
/// the structural rail (DI registration, host loop, claim + payload
/// shape) so the real image-analysis dispatch can drop in without
/// touching the platform wiring.
/// </para>
/// <para>
/// <b>What lands later.</b> The actual call into the inference pipeline
/// (today routed through <c>NickERP.Inspection.Inference.*</c> plugin
/// adapters keyed on <c>ScannerThresholdProfile</c> + <c>TypeCode</c>)
/// is intentionally deferred — that integration carries its own
/// design questions (subprocess vs. in-proc inference, plugin contract
/// version, finding-write transactionality) that aren't blocked by
/// this scaffold.
/// </para>
/// <para>
/// <b>Stage role.</b> Consumes work items that have entered
/// <see cref="NickERP.Inspection.Core.Entities.InspectionWorkflowState.Validated"/>
/// after split-detection has settled. Produces findings that the
/// downstream <see cref="DecisionAgentConsumer"/> reads to score the
/// case.
/// </para>
/// <para>
/// <b>Idempotency contract.</b> The placeholder body is naturally
/// idempotent (logging only). When the real inference call lands, it
/// MUST stay idempotent under retry — janitor reclaims, lease
/// expiries, and worker restarts all replay the same claim, and the
/// queueing layer guarantees at-least-once not exactly-once delivery.
/// </para>
/// <para>
/// <b>Throw to fail.</b> The host wraps the call and routes thrown
/// exceptions to <see cref="IQueueClaim{TPayload}.FailAsync"/>; consumers
/// don't call Fail directly. The placeholder body never throws, so
/// every claim auto-completes — that's the proof-of-life signal until
/// the real body lands.
/// </para>
/// </remarks>
public sealed class ImageAnalysisConsumer : IQueueConsumer<ImageAnalysisPayload>
{
    private readonly ILogger<ImageAnalysisConsumer> _logger;

    /// <summary>
    /// Construct the consumer. Registered as scoped — the platform host
    /// resolves a fresh instance per claimed row, so capturing the
    /// per-row claim in instance state would be unsafe but isn't done
    /// here.
    /// </summary>
    /// <param name="logger">DI-supplied logger; used for the proof-of-life trace.</param>
    public ImageAnalysisConsumer(ILogger<ImageAnalysisConsumer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task ProcessAsync(IQueueClaim<ImageAnalysisPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // TODO[Sprint S+3]: implement ImageAnalysisConsumer — dispatch
        // inference via the resolved IInferenceAdapter for the case's
        // scanner type, persist findings, advance the case to
        // analyst-ready. Sprint S+3 placeholder logs once + completes
        // so the queue plumbing is exercisable end-to-end.
        _logger.LogInformation(
            "TODO[Sprint S+3]: implement ImageAnalysisConsumer — placeholder ran for CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount} (no-op — real inference dispatch lands in a later sprint)",
            claim.Payload.CaseId,
            claim.WorkItemId,
            claim.AttemptCount);

        return Task.CompletedTask;
    }
}
