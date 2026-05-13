using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Queueing.Entities;
using Stateless;
using Stateless.Graph;

namespace NickERP.Platform.Queueing.StateMachine;

/// <summary>
/// Sole-writer base for module-specific state machines that operate on
/// a <see cref="WorkItem{TState}"/>. Subclasses configure a Stateless
/// <c>StateMachine&lt;TState,TTrigger&gt;</c> describing the legal
/// transition graph; this base class wraps every transition in a single
/// DB transaction that updates the work item, writes a
/// <see cref="WorkItemTransition"/> audit row, and gives the subclass a
/// chance to enqueue downstream work — atomically.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Stateless.</b> Compile-time-checked triggers (the trigger enum
/// is closed); guard clauses; transition callbacks; Graphviz export for
/// design review. The library is small (zero deps), MIT-licensed, and
/// stable. Adding it lets us replace v1's hand-rolled
/// <c>AnalysisStatusValidator</c> + 37 ad-hoc <c>g.Status =</c> writers
/// with a single configured graph that throws on illegal transitions
/// out of the box.
/// </para>
/// <para>
/// <b>Sole-writer enforcement.</b> <see cref="WorkItem{TState}.CurrentState"/>'s
/// setter is <c>internal</c>. Stateless's <c>OnTransitioned</c> callback
/// fires inside this assembly so we can call <see cref="WorkItem{TState}.SetState"/>;
/// nothing outside this assembly can write the field.
/// </para>
/// <para>
/// <b>Audit trail.</b> Every applied transition writes one
/// <see cref="WorkItemTransition"/> row with from/to states, trigger,
/// actor, and reason. Reason is <b>required</b> on every call — there's
/// no overload that lets a caller skip it. System actors pass tags like
/// <c>"lease-expired"</c>; human actors pass whatever the UI captured.
/// </para>
/// <para>
/// <b>Idempotency anchor.</b> <see cref="WorkItem{TState}.IdempotencyAnchor"/>
/// shapes the keys for any queue rows the subclass enqueues from inside
/// a transition (typical pattern:
/// <c>IdempotencyKey.From(workItem.IdempotencyAnchor, trigger, toState)</c>).
/// Subclasses override the transaction-aware <c>OnTransitionedAsync</c>
/// overload to do the actual enqueueing.
/// </para>
/// </remarks>
/// <typeparam name="TState">State enum the machine operates on.</typeparam>
/// <typeparam name="TTrigger">Trigger enum the machine accepts.</typeparam>
/// <typeparam name="TWorkItem">Concrete work-item type (subclass of <see cref="WorkItem{TState}"/>).</typeparam>
public abstract class WorkItemStateMachine<TState, TTrigger, TWorkItem>
    where TState : struct, Enum
    where TTrigger : struct, Enum
    where TWorkItem : WorkItem<TState>
{
    /// <summary>
    /// Configure the legal transition graph. Called once per machine
    /// instance, against a Stateless <c>StateMachine</c> already
    /// wired up to read/write the work item's <c>CurrentState</c>.
    /// Subclasses define <c>Permit</c> / <c>PermitIf</c> / <c>OnEntry</c>
    /// in this method.
    /// </summary>
    /// <param name="machine">The Stateless instance to configure.</param>
    /// <param name="workItem">
    /// The work item being transitioned — passed so guards can branch
    /// on its data (e.g. "permit Submit only if Verdict is Approved").
    /// </param>
    protected abstract void Configure(StateMachine<TState, TTrigger> machine, TWorkItem workItem);

    /// <summary>
    /// Hook for subclasses to schedule downstream work as part of a
    /// transition. Called <b>inside</b> the transition's DB transaction
    /// (after the work-item update + audit row write, before commit).
    /// Throwing here aborts the whole transition — the work item
    /// reverts and no audit row persists.
    /// </summary>
    /// <remarks>
    /// Typical implementation:
    /// <code>
    /// protected override async Task OnTransitionedAsync(...)
    /// {
    ///     if (toState == InspectionWorkflowState.Validated)
    ///     {
    ///         await _splitDetectionQueue.EnqueueAsync(db, new EnqueueRequest&lt;...&gt; {
    ///             WorkItemId = workItem.Id,
    ///             Payload = new SplitDetectionPayload(workItem.CaseId),
    ///             IdempotencyKey = IdempotencyKey.From(workItem.IdempotencyAnchor, "split", toState),
    ///             CorrelationId = correlationId
    ///         }, ct);
    ///     }
    /// }
    /// </code>
    /// </remarks>
    protected virtual Task OnTransitionedAsync(
        TWorkItem workItem,
        TState fromState,
        TState toState,
        TTrigger trigger,
        string actor,
        string reason,
        string? correlationId,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Transaction-aware hook for subclasses to schedule downstream work.
    /// New producer side effects should override this overload and use
    /// <paramref name="db"/>'s current transaction.
    /// </summary>
    protected virtual Task OnTransitionedAsync(
        DbContext db,
        TWorkItem workItem,
        TState fromState,
        TState toState,
        TTrigger trigger,
        string actor,
        string reason,
        string? correlationId,
        CancellationToken ct) =>
        OnTransitionedAsync(workItem, fromState, toState, trigger, actor, reason, correlationId, ct);

    /// <summary>
    /// Atomically transition the work item via <paramref name="trigger"/>.
    /// Writes the new state, the audit row, and any subclass-spawned
    /// queue rows in one DB transaction. The work item's
    /// <c>Version</c> is bumped — concurrent transitions on the same
    /// row see <see cref="StateTransitionOutcome.Conflict"/>.
    /// </summary>
    /// <param name="db">
    /// The DbContext that tracks <paramref name="workItem"/>. The work
    /// item must be tracked (not <c>AsNoTracking</c>) — see v1 memory
    /// <c>feedback_application_dbcontext_notracking_default.md</c> for
    /// the gotcha that motivated this requirement.
    /// </param>
    /// <param name="workItem">The (tracked) work item to transition.</param>
    /// <param name="trigger">Trigger to fire.</param>
    /// <param name="actor">
    /// User id (Guid string) for human actors; service name (e.g.
    /// <c>"DECISION-AGENT"</c>, <c>"QueueJanitor"</c>) for system
    /// actors. Required.
    /// </param>
    /// <param name="reason">
    /// Free-text justification (≤500 chars). Required — the audit log's
    /// usefulness depends on this being populated.
    /// </param>
    /// <param name="correlationId">Optional correlation id propagated to the audit row + spawned queue rows.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<StateTransitionResult<TState>> TransitionAsync(
        DbContext db,
        TWorkItem workItem,
        TTrigger trigger,
        string actor,
        string reason,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(workItem);
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Actor is required (the audit log isn't useful without it).", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required (the audit log isn't useful without it).", nameof(reason));

        var fromState = workItem.CurrentState;

        // Build a fresh Stateless instance for this transition. Stateless
        // is cheap to construct; instance-per-call avoids subtle bugs
        // around configurations that depend on the work item's data.
        var machine = new StateMachine<TState, TTrigger>(
            stateAccessor: () => workItem.CurrentState,
            stateMutator: s => workItem.SetState(s));
        Configure(machine, workItem);

        // Capture guard reason if Stateless rejects.
        string? guardReason = null;
        machine.OnUnhandledTrigger((state, t, unmetGuards) =>
        {
            guardReason = unmetGuards is { Count: > 0 }
                ? string.Join("; ", unmetGuards)
                : $"Trigger '{t}' is not permitted from state '{state}'.";
        });

        if (!machine.CanFire(trigger))
        {
            return new StateTransitionResult<TState>
            {
                Outcome = StateTransitionOutcome.NotPermitted,
                FromState = fromState,
                ToState = fromState,
                GuardReason = guardReason ?? $"Trigger '{trigger}' is not permitted from state '{fromState}'."
            };
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // Fire — Stateless invokes its own callbacks and the state
            // mutator; if a guard fails, it throws InvalidOperationException
            // unless OnUnhandledTrigger captured it (which we configured).
            await machine.FireAsync(trigger).ConfigureAwait(false);

            if (EqualityComparer<TState>.Default.Equals(workItem.CurrentState, fromState))
            {
                // No state change — guard must have failed silently.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return new StateTransitionResult<TState>
                {
                    Outcome = StateTransitionOutcome.GuardFailed,
                    FromState = fromState,
                    ToState = fromState,
                    GuardReason = guardReason ?? "Guard clause prevented the transition."
                };
            }

            workItem.Version++;
            workItem.UpdatedAt = DateTimeOffset.UtcNow;

            var transition = new WorkItemTransition
            {
                TenantId = workItem.TenantId,
                WorkItemId = workItem.Id,
                FromState = fromState.ToString(),
                ToState = workItem.CurrentState.ToString(),
                Trigger = trigger.ToString(),
                Actor = actor,
                Reason = reason,
                CorrelationId = correlationId,
                OccurredAt = DateTimeOffset.UtcNow
            };
            db.Set<WorkItemTransition>().Add(transition);

            // Subclass hook — runs before SaveChanges so any queue rows
            // it inserts go in the same transaction.
            await OnTransitionedAsync(
                db, workItem, fromState, workItem.CurrentState, trigger,
                actor, reason, correlationId, ct).ConfigureAwait(false);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            return new StateTransitionResult<TState>
            {
                Outcome = StateTransitionOutcome.Applied,
                FromState = fromState,
                ToState = workItem.CurrentState,
                TransitionId = transition.Id
            };
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return new StateTransitionResult<TState>
            {
                Outcome = StateTransitionOutcome.Conflict,
                FromState = fromState,
                ToState = fromState,
                GuardReason = "DbUpdateConcurrencyException — another process bumped Version."
            };
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Render the transition graph as Graphviz DOT for design-time review.
    /// Useful for the docs/ARCHITECTURE.md design artifact and for
    /// validating parity with v1's transition table during cutover.
    /// </summary>
    /// <remarks>
    /// Graphviz output is purely informational — it does not include
    /// guard clauses' runtime data. Inspect the source <c>Configure</c>
    /// method for the full picture.
    /// </remarks>
    public string ExportToDot(TWorkItem workItemForGuardConfig)
    {
        ArgumentNullException.ThrowIfNull(workItemForGuardConfig);
        var machine = new StateMachine<TState, TTrigger>(workItemForGuardConfig.CurrentState);
        Configure(machine, workItemForGuardConfig);
        return UmlDotGraph.Format(machine.GetInfo());
    }
}
