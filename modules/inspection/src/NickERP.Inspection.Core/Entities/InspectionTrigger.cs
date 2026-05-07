namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Closed set of triggers the <c>InspectionStateMachine</c> accepts. Each
/// value drives the work item from one
/// <see cref="InspectionWorkflowState"/> to the next; the state machine's
/// <c>Configure</c> wires the <c>Permit</c> graph that maps each trigger
/// to its destination state. Compile-time-checked: the state machine
/// won't compile if a trigger doesn't appear in this enum.
/// </summary>
public enum InspectionTrigger
{
    /// <summary>
    /// Authority documents were fetched and the completeness rules
    /// passed — drives <see cref="InspectionWorkflowState.Open"/> →
    /// <see cref="InspectionWorkflowState.Validated"/>. Fires the
    /// split-detection enqueue from
    /// <c>InspectionStateMachine.OnTransitionedAsync</c>.
    /// </summary>
    Validate,

    /// <summary>
    /// An analyst picked up the case (or it was assigned by the
    /// supervisor) — drives
    /// <see cref="InspectionWorkflowState.Validated"/> →
    /// <see cref="InspectionWorkflowState.Assigned"/>.
    /// </summary>
    Assign,

    /// <summary>
    /// Analyst finished the review and submitted findings — drives
    /// <see cref="InspectionWorkflowState.Assigned"/> →
    /// <see cref="InspectionWorkflowState.Reviewed"/>.
    /// </summary>
    ReviewSubmitted,

    /// <summary>
    /// Verdict (Approved / Rejected / Hold) was recorded — drives
    /// <see cref="InspectionWorkflowState.Reviewed"/> →
    /// <see cref="InspectionWorkflowState.Verdict"/>.
    /// </summary>
    VerdictRecorded,

    /// <summary>
    /// External authority system accepted the outbound submission —
    /// drives <see cref="InspectionWorkflowState.Verdict"/> →
    /// <see cref="InspectionWorkflowState.Submitted"/>.
    /// </summary>
    OutboundAccepted,

    /// <summary>
    /// Case is finalised — drives
    /// <see cref="InspectionWorkflowState.Submitted"/> →
    /// <see cref="InspectionWorkflowState.Closed"/>. Terminal.
    /// </summary>
    Close,

    /// <summary>
    /// Case is abandoned (wrong subject, manual cancel, scanner
    /// pulled the wrong record, etc.) — drives any non-terminal
    /// state to <see cref="InspectionWorkflowState.Cancelled"/>.
    /// Terminal.
    /// </summary>
    Cancel
}
