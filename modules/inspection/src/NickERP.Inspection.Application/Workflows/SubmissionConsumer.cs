using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Sprint S+3 / B-queues — placeholder consumer for the
/// <c>inspection.queue_submission</c> queue. Implements
/// <see cref="IQueueConsumer{TPayload}"/> for
/// <see cref="OutboundSubmissionPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is right now.</b> A compile-clean stub that logs a
/// single TODO once on first claim then completes successfully. Lays
/// the structural rail (DI registration, host loop, claim + payload
/// shape) so the real outbound dispatch can drop in without touching
/// the platform wiring.
/// </para>
/// <para>
/// <b>What lands later.</b> The actual outbound dispatch (today
/// routed through <c>IExternalSystemAdapter</c> plugins keyed on
/// <c>ExternalSystemInstance.TypeCode</c>; for Ghana that's
/// <c>NickERP.Inspection.ExternalSystems.IcumsGh</c>) is intentionally
/// deferred — that integration carries its own design questions
/// (priority lane resolution, retry budget, signed-envelope formation
/// via <c>IcumsSigningKey</c>, post-hoc outcome reconciliation) that
/// aren't blocked by this scaffold.
/// </para>
/// <para>
/// <b>Stage role.</b> Consumes work items whose verdict has been
/// recorded by <see cref="AuditReviewConsumer"/> and pushes the
/// submission envelope to the bound external authority system. On
/// success the case advances to
/// <see cref="NickERP.Inspection.Core.Entities.InspectionWorkflowState.Submitted"/>;
/// on transient failure it requeues per the platform retry budget;
/// on terminal failure it dead-letters for manual ops review.
/// </para>
/// <para>
/// <b>Idempotency contract.</b> The placeholder body is naturally
/// idempotent (logging only). When the real submission call lands, it
/// MUST stay idempotent under retry — janitor reclaims, lease
/// expiries, and worker restarts all replay the same claim, and the
/// queueing layer guarantees at-least-once not exactly-once delivery.
/// External authority systems are usually NOT idempotent natively, so
/// the consumer must dedup at the adapter boundary using the
/// <see cref="IQueueClaim{TPayload}.WorkItemId"/> +
/// <see cref="IQueueClaim{TPayload}.AttemptCount"/> as inputs to a
/// stable submission-id.
/// </para>
/// <para>
/// <b>Throw to fail.</b> The host wraps the call and routes thrown
/// exceptions to <see cref="IQueueClaim{TPayload}.FailAsync"/>; consumers
/// don't call Fail directly. The placeholder body never throws, so
/// every claim auto-completes — that's the proof-of-life signal until
/// the real body lands.
/// </para>
/// </remarks>
public sealed class SubmissionConsumer : IQueueConsumer<OutboundSubmissionPayload>
{
    private readonly ILogger<SubmissionConsumer> _logger;

    /// <summary>
    /// Construct the consumer. Registered as scoped — the platform host
    /// resolves a fresh instance per claimed row, so capturing the
    /// per-row claim in instance state would be unsafe but isn't done
    /// here.
    /// </summary>
    /// <param name="logger">DI-supplied logger; used for the proof-of-life trace.</param>
    public SubmissionConsumer(ILogger<SubmissionConsumer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task ProcessAsync(IQueueClaim<OutboundSubmissionPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // TODO[Sprint S+3]: implement SubmissionConsumer — resolve the
        // bound IExternalSystemAdapter for the case's location +
        // tenant, build the signed submission envelope, dispatch to
        // the external authority system, persist the OutboundSubmission
        // row, advance the case to Submitted on success. Sprint S+3
        // placeholder logs once + completes so the queue plumbing is
        // exercisable end-to-end.
        _logger.LogInformation(
            "TODO[Sprint S+3]: implement SubmissionConsumer — placeholder ran for CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount} (no-op — real outbound dispatch lands in a later sprint)",
            claim.Payload.CaseId,
            claim.WorkItemId,
            claim.AttemptCount);

        return Task.CompletedTask;
    }
}
