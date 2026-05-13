using Microsoft.EntityFrameworkCore;
using NickERP.Inspection.Application.Workflows;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Queueing.StateMachine;
using Stateless;

namespace NickERP.Inspection.Application.StateMachines;

/// <summary>
/// Sole writer for the <see cref="InspectionWorkflowState"/> graph on an
/// <see cref="InspectionWorkItem"/>. Subclasses
/// <see cref="WorkItemStateMachine{TState, TTrigger, TWorkItem}"/> closed
/// over (<see cref="InspectionWorkflowState"/>, <see cref="InspectionTrigger"/>,
/// <see cref="InspectionWorkItem"/>) — the platform base owns the DB
/// transaction, audit-row writes, and version bump; this subclass owns
/// the legal transition graph and the side-effect (split-detection
/// enqueue) on <see cref="InspectionWorkflowState.Validated"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The graph.</b> Mirrors <see cref="InspectionWorkflowState"/>:
/// <list type="bullet">
/// <item><description><c>Open</c> →[<c>Validate</c>]→ <c>Validated</c></description></item>
/// <item><description><c>Validated</c> →[<c>Assign</c>]→ <c>Assigned</c></description></item>
/// <item><description><c>Assigned</c> →[<c>ReviewSubmitted</c>]→ <c>Reviewed</c></description></item>
/// <item><description><c>Reviewed</c> →[<c>VerdictRecorded</c>]→ <c>Verdict</c></description></item>
/// <item><description><c>Verdict</c> →[<c>OutboundAccepted</c>]→ <c>Submitted</c></description></item>
/// <item><description><c>Submitted</c> →[<c>Close</c>]→ <c>Closed</c> (terminal)</description></item>
/// <item><description>any non-terminal state →[<c>Cancel</c>]→ <c>Cancelled</c> (terminal)</description></item>
/// </list>
/// Stateless rejects any trigger not in the configured graph; the base
/// returns <see cref="StateTransitionOutcome.NotPermitted"/> with a
/// human-readable reason. <c>Closed</c> and <c>Cancelled</c> are
/// terminal — no triggers wire out of them.
/// </para>
/// <para>
/// <b>Side effect on <c>Validate</c>.</b> The
/// <see cref="OnTransitionedAsync"/> override enqueues a
/// <see cref="SplitDetectionPayload"/> when the work item enters
/// <see cref="InspectionWorkflowState.Validated"/>. The enqueue happens
/// inside the platform base's transaction (see the base's
/// <c>TransitionAsync</c>), so the queue insert and the state change
/// commit atomically — if the enqueue throws, the state change rolls
/// back. Idempotency key is derived from the work item's
/// <see cref="NickERP.Platform.Queueing.Entities.WorkItem{TState}.IdempotencyAnchor"/>
/// + the trigger + the destination state, so a retry of the same
/// transition collapses to one queue row.
/// </para>
/// <para>
/// <b>Why sealed.</b> Single-purpose: this class owns the inspection
/// workflow's graph and exactly one side effect today. If a second side
/// effect is needed (e.g. notify an analyst on <c>Assigned</c>), it
/// lives inside this same <see cref="OnTransitionedAsync"/> override —
/// not in a subclass — to keep all transition side effects in one
/// auditable place.
/// </para>
/// <para>
/// <b>ImageRef placeholder.</b> The Sprint 14 payload requires an
/// <see cref="SplitDetectionPayload.ImageRef"/> string. The
/// vendor-neutral image-resolution layer hasn't landed in v2 yet — the
/// real ref will come from <c>NickERP.Inspection.Imaging</c> once the
/// per-case primary-artifact selector is exposed to the state-machine
/// layer. Until then we ship a deterministic placeholder derived from
/// the case id (so payloads are still distinguishable in tests + logs)
/// and let the consumer's placeholder body log the value. The string
/// shape changes when the imaging layer is wired through; the field is
/// not interpreted at the queueing layer so the wire format is forward-
/// compatible.
/// </para>
/// </remarks>
public sealed class InspectionStateMachine
    : WorkItemStateMachine<InspectionWorkflowState, InspectionTrigger, InspectionWorkItem>
{
    private readonly ITransactionalQueue<SplitDetectionPayload> _splitDetectionQueue;

    /// <summary>
    /// Construct the state machine. <paramref name="splitDetectionQueue"/>
    /// is required — the <c>Validate</c> transition depends on it; failing
    /// to register it surfaces here at host build time rather than at the
    /// first transition.
    /// </summary>
    /// <param name="splitDetectionQueue">
    /// Producer surface for <c>inspection.queue_split_detection</c>.
    /// Resolved from DI as the platform's
    /// <see cref="ITransactionalQueue{TPayload}"/> binding for
    /// <see cref="SplitDetectionPayload"/>.
    /// </param>
    public InspectionStateMachine(ITransactionalQueue<SplitDetectionPayload> splitDetectionQueue)
    {
        _splitDetectionQueue = splitDetectionQueue
            ?? throw new ArgumentNullException(nameof(splitDetectionQueue));
    }

    /// <inheritdoc />
    protected override void Configure(
        StateMachine<InspectionWorkflowState, InspectionTrigger> machine,
        InspectionWorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(workItem);

        // Forward path: Open → Validated → Assigned → Reviewed →
        // Verdict → Submitted → Closed.
        machine.Configure(InspectionWorkflowState.Open)
            .Permit(InspectionTrigger.Validate, InspectionWorkflowState.Validated)
            .Permit(InspectionTrigger.Cancel, InspectionWorkflowState.Cancelled);

        machine.Configure(InspectionWorkflowState.Validated)
            .Permit(InspectionTrigger.Assign, InspectionWorkflowState.Assigned)
            .Permit(InspectionTrigger.Cancel, InspectionWorkflowState.Cancelled);

        machine.Configure(InspectionWorkflowState.Assigned)
            .Permit(InspectionTrigger.ReviewSubmitted, InspectionWorkflowState.Reviewed)
            .Permit(InspectionTrigger.Cancel, InspectionWorkflowState.Cancelled);

        machine.Configure(InspectionWorkflowState.Reviewed)
            .Permit(InspectionTrigger.VerdictRecorded, InspectionWorkflowState.Verdict)
            .Permit(InspectionTrigger.Cancel, InspectionWorkflowState.Cancelled);

        machine.Configure(InspectionWorkflowState.Verdict)
            .Permit(InspectionTrigger.OutboundAccepted, InspectionWorkflowState.Submitted)
            .Permit(InspectionTrigger.Cancel, InspectionWorkflowState.Cancelled);

        machine.Configure(InspectionWorkflowState.Submitted)
            .Permit(InspectionTrigger.Close, InspectionWorkflowState.Closed)
            .Permit(InspectionTrigger.Cancel, InspectionWorkflowState.Cancelled);

        // Terminal states — Stateless requires a Configure() call so the
        // graph export sees them, but no triggers wire out of either.
        machine.Configure(InspectionWorkflowState.Closed);
        machine.Configure(InspectionWorkflowState.Cancelled);
    }

    /// <inheritdoc />
    protected override async Task OnTransitionedAsync(
        DbContext db,
        InspectionWorkItem workItem,
        InspectionWorkflowState fromState,
        InspectionWorkflowState toState,
        InspectionTrigger trigger,
        string actor,
        string reason,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        if (db is InspectionDbContext inspectionDb)
        {
            var @case = await inspectionDb.Cases
                .FirstOrDefaultAsync(c => c.Id == workItem.CaseId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Inspection work item {workItem.Id} points at missing case {workItem.CaseId}.");

            @case.State = toState;
            @case.StateEnteredAt = DateTimeOffset.UtcNow;
            if (toState is InspectionWorkflowState.Closed or InspectionWorkflowState.Cancelled)
            {
                @case.ClosedAt ??= @case.StateEnteredAt;
            }
        }

        // Sprint 14 / B-queues — only side effect right now is the
        // split-detection enqueue on Open → Validated. Add new effects
        // here (not in a subclass) so every transition side effect lives
        // in one auditable place.
        if (toState != InspectionWorkflowState.Validated)
        {
            return;
        }

        // ImageRef placeholder — see class-level remarks. Deterministic
        // per case so test assertions can match it; consumer logs it but
        // does not parse it.
        var imageRef = $"case://{workItem.CaseId:N}/primary";

        var idempotencyKey = IdempotencyKey.From(
            workItem.IdempotencyAnchor,
            trigger.ToString(),
            toState.ToString());

        await _splitDetectionQueue.EnqueueAsync(
            db,
            new EnqueueRequest<SplitDetectionPayload>
            {
                WorkItemId = workItem.Id,
                Payload = new SplitDetectionPayload(workItem.CaseId, imageRef),
                IdempotencyKey = idempotencyKey,
                CorrelationId = correlationId
            },
            ct).ConfigureAwait(false);
    }
}
