using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Sprint S+3 / B-queues — placeholder consumer for the
/// <c>inspection.queue_audit_assignment</c> queue. Implements
/// <see cref="IQueueConsumer{TPayload}"/> for
/// <see cref="AuditAssignmentPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is right now.</b> A compile-clean stub that logs a
/// single TODO once on first claim then completes successfully. Lays
/// the structural rail (DI registration, host loop, claim + payload
/// shape) so the real auditor selection algorithm can drop in without
/// touching the platform wiring.
/// </para>
/// <para>
/// <b>What lands later.</b> The actual auditor selection (today the
/// v1 ready-list-with-heartbeat algorithm — <c>IsReady=true</c> +
/// recent heartbeat — falling through to <c>SYSTEM-HOUSEKEEPING</c>
/// synthetic audit rows when no auditor is ready) is intentionally
/// deferred — that integration carries its own design questions
/// (location-scoped routing, dead-mans-switch posture, claim
/// concurrency under multi-host workers) that aren't blocked by this
/// scaffold.
/// </para>
/// <para>
/// <b>Stage role.</b> Consumes work items whose decision has been
/// recorded by <see cref="DecisionAgentConsumer"/> and routes them to
/// a ready auditor — creating the <c>ReviewSession</c> that the
/// downstream <see cref="AuditReviewConsumer"/> picks up after the
/// auditor submits.
/// </para>
/// <para>
/// <b>Idempotency contract.</b> The placeholder body is naturally
/// idempotent (logging only). When the real assignment call lands, it
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
public sealed class AuditAssignmentConsumer : IQueueConsumer<AuditAssignmentPayload>
{
    private readonly ILogger<AuditAssignmentConsumer> _logger;

    /// <summary>
    /// Construct the consumer. Registered as scoped — the platform host
    /// resolves a fresh instance per claimed row, so capturing the
    /// per-row claim in instance state would be unsafe but isn't done
    /// here.
    /// </summary>
    /// <param name="logger">DI-supplied logger; used for the proof-of-life trace.</param>
    public AuditAssignmentConsumer(ILogger<AuditAssignmentConsumer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task ProcessAsync(IQueueClaim<AuditAssignmentPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // TODO[Sprint S+3]: implement AuditAssignmentConsumer — query
        // ready auditors (IsReady=true + recent heartbeat), pick by
        // location + load, create ReviewSession; fall back to
        // SYSTEM-HOUSEKEEPING sweep when none ready. Sprint S+3
        // placeholder logs once + completes so the queue plumbing is
        // exercisable end-to-end.
        _logger.LogInformation(
            "TODO[Sprint S+3]: implement AuditAssignmentConsumer — placeholder ran for CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount} (no-op — real auditor selection lands in a later sprint)",
            claim.Payload.CaseId,
            claim.WorkItemId,
            claim.AttemptCount);

        return Task.CompletedTask;
    }
}
