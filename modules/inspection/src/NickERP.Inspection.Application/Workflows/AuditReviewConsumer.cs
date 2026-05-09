using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Sprint S+3 / B-queues — placeholder consumer for the
/// <c>inspection.queue_audit_review</c> queue. Implements
/// <see cref="IQueueConsumer{TPayload}"/> for
/// <see cref="AuditReviewPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is right now.</b> A compile-clean stub that logs a
/// single TODO once on first claim then completes successfully. Lays
/// the structural rail (DI registration, host loop, claim + payload
/// shape) so the real auditor disposition recorder can drop in without
/// touching the platform wiring.
/// </para>
/// <para>
/// <b>What lands later.</b> The actual auditor disposition pipeline
/// (delta-vs-analyst comparison, escalation triggers, second-opinion
/// routing on disagreement, final verdict emission) is intentionally
/// deferred — that integration carries its own design questions
/// (multi-auditor consensus, hold-state semantics, audit-immutability
/// hooks) that aren't blocked by this scaffold.
/// </para>
/// <para>
/// <b>Stage role.</b> Consumes work items where an auditor has
/// submitted a disposition against the analyst's findings (concur /
/// disagree / hold). On concur the case advances to verdict; on
/// disagree it routes for second-opinion or escalation; on hold it
/// stays in audit until released. Verdict emission flows downstream
/// to <see cref="SubmissionConsumer"/>.
/// </para>
/// <para>
/// <b>Idempotency contract.</b> The placeholder body is naturally
/// idempotent (logging only). When the real recorder call lands, it
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
public sealed class AuditReviewConsumer : IQueueConsumer<AuditReviewPayload>
{
    private readonly ILogger<AuditReviewConsumer> _logger;

    /// <summary>
    /// Construct the consumer. Registered as scoped — the platform host
    /// resolves a fresh instance per claimed row, so capturing the
    /// per-row claim in instance state would be unsafe but isn't done
    /// here.
    /// </summary>
    /// <param name="logger">DI-supplied logger; used for the proof-of-life trace.</param>
    public AuditReviewConsumer(ILogger<AuditReviewConsumer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task ProcessAsync(IQueueClaim<AuditReviewPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // TODO[Sprint S+3]: implement AuditReviewConsumer — read the
        // auditor's submitted disposition, compute delta vs analyst
        // findings, route concur/disagree/hold to the correct
        // downstream state, emit verdict on concur. Sprint S+3
        // placeholder logs once + completes so the queue plumbing is
        // exercisable end-to-end.
        _logger.LogInformation(
            "TODO[Sprint S+3]: implement AuditReviewConsumer — placeholder ran for CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount} (no-op — real audit-review pipeline lands in a later sprint)",
            claim.Payload.CaseId,
            claim.WorkItemId,
            claim.AttemptCount);

        return Task.CompletedTask;
    }
}
