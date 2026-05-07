namespace NickERP.Platform.Queueing.StateMachine;

/// <summary>
/// Outcome of a single
/// <c>WorkItemStateMachine&lt;TState,TTrigger&gt;.TransitionAsync</c>
/// call. Returned by reference (not thrown) so callers can branch on
/// the outcome cheaply — exceptions are reserved for genuinely unexpected
/// conditions like DB failure.
/// </summary>
public enum StateTransitionOutcome
{
    /// <summary>Transition was legal and was committed. The work item is in the new state.</summary>
    Applied = 0,

    /// <summary>
    /// Transition was rejected because the trigger isn't permitted from
    /// the current state. The work item is unchanged. This is a normal
    /// outcome — it happens when two workers race the same trigger and
    /// one gets there first (the loser sees the new state and bails).
    /// </summary>
    NotPermitted = 1,

    /// <summary>
    /// Transition's guard clause returned <c>false</c>. The work item is
    /// unchanged. The guard's reason (e.g. "auditor not signed in",
    /// "downstream queue at capacity") is captured in
    /// <see cref="StateTransitionResult{TState}.GuardReason"/>.
    /// </summary>
    GuardFailed = 2,

    /// <summary>
    /// EF Core <c>DbUpdateConcurrencyException</c> on save — another
    /// process bumped <c>WorkItem.Version</c> between this caller's
    /// read and write. The transition was NOT applied; the caller should
    /// reload the work item and decide whether to retry.
    /// </summary>
    Conflict = 3
}

/// <summary>
/// Detailed result of a transition attempt. Always populated, even for
/// non-Applied outcomes, so audit / telemetry can capture what happened.
/// </summary>
/// <typeparam name="TState">The state machine's state enum.</typeparam>
public sealed class StateTransitionResult<TState>
    where TState : struct, Enum
{
    /// <summary>What happened.</summary>
    public required StateTransitionOutcome Outcome { get; init; }

    /// <summary>The state the work item was in BEFORE the attempt.</summary>
    public required TState FromState { get; init; }

    /// <summary>
    /// The state the work item is in AFTER the attempt. Equal to
    /// <see cref="FromState"/> for any non-Applied outcome.
    /// </summary>
    public required TState ToState { get; init; }

    /// <summary>
    /// Free-text reason for non-Applied outcomes (guard message, race
    /// description). Null for <see cref="StateTransitionOutcome.Applied"/>.
    /// </summary>
    public string? GuardReason { get; init; }

    /// <summary>
    /// Id of the audit row written for an Applied transition; null
    /// otherwise.
    /// </summary>
    public long? TransitionId { get; init; }
}
