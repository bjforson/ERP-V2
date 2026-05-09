using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Sprint S+3 / B-queues — placeholder consumer for the
/// <c>inspection.queue_decision_agent</c> queue. Implements
/// <see cref="IQueueConsumer{TPayload}"/> for
/// <see cref="DecisionAgentPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is right now.</b> A compile-clean stub that logs a
/// single TODO once on first claim then completes successfully. Lays
/// the structural rail (DI registration, host loop, claim + payload
/// shape) so the real decision-agent scoring pass can drop in without
/// touching the platform wiring.
/// </para>
/// <para>
/// <b>What lands later.</b> The actual call into the rules-based
/// decision agent (today shadow-mode by config; honouring the
/// <c>decisionagentsettings</c> singleton's posture) is intentionally
/// deferred — that integration carries its own design questions
/// (live vs. shadow, condition-weight resolution from settings,
/// audit-trail emission to <c>auditdecisions</c>) that aren't blocked
/// by this scaffold.
/// </para>
/// <para>
/// <b>Stage role.</b> Consumes work items whose findings have landed
/// from <see cref="ImageAnalysisConsumer"/> and applies the
/// rules-based scoring pass. In shadow mode the decision is logged
/// but no auto-advance happens; in live mode the case advances to
/// audit assignment via <see cref="AuditAssignmentConsumer"/>.
/// </para>
/// <para>
/// <b>Idempotency contract.</b> The placeholder body is naturally
/// idempotent (logging only). When the real scoring call lands, it
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
public sealed class DecisionAgentConsumer : IQueueConsumer<DecisionAgentPayload>
{
    private readonly ILogger<DecisionAgentConsumer> _logger;

    /// <summary>
    /// Construct the consumer. Registered as scoped — the platform host
    /// resolves a fresh instance per claimed row, so capturing the
    /// per-row claim in instance state would be unsafe but isn't done
    /// here.
    /// </summary>
    /// <param name="logger">DI-supplied logger; used for the proof-of-life trace.</param>
    public DecisionAgentConsumer(ILogger<DecisionAgentConsumer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task ProcessAsync(IQueueClaim<DecisionAgentPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // TODO[Sprint S+3]: implement DecisionAgentConsumer — read
        // decisionagentsettings, evaluate condition weights, emit
        // shadow or live decision, persist auditdecisions row when
        // non-shadow. Sprint S+3 placeholder logs once + completes so
        // the queue plumbing is exercisable end-to-end.
        _logger.LogInformation(
            "TODO[Sprint S+3]: implement DecisionAgentConsumer — placeholder ran for CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount} (no-op — real decision-agent scoring lands in a later sprint)",
            claim.Payload.CaseId,
            claim.WorkItemId,
            claim.AttemptCount);

        return Task.CompletedTask;
    }
}
