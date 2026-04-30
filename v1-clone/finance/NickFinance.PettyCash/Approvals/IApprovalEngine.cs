namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// The pluggable surface that decides how a voucher gets approved. The
/// default <see cref="SingleStepApprovalEngine"/> matches the original
/// MVP-zero behaviour (one step, any approver). The
/// <see cref="PolicyApprovalEngine"/> walks an <see cref="ApprovalPolicy"/>
/// to produce an N-step chain.
/// </summary>
public interface IApprovalEngine
{
    /// <summary>
    /// Build the approval chain at submit time. Returns the steps in
    /// order; an empty list means "auto-approve". Returning a step with
    /// <see cref="ApprovalDecision.UnfillableRole"/> + a non-empty
    /// <see cref="VoucherApproval.Role"/> tells the service to mark the
    /// voucher as Rejected immediately.
    /// </summary>
    IReadOnlyList<VoucherApproval> Plan(Voucher voucher);

    /// <summary>
    /// Apply a decision on the current pending step. Returns the next
    /// state for the voucher: still in approval, fully approved, or
    /// rejected. The engine is responsible for matching the decider
    /// against the assigned approver and may relax that match (e.g.
    /// during delegation in v1.2).
    /// </summary>
    ApprovalAdvance Decide(
        Voucher voucher,
        IReadOnlyList<VoucherApproval> steps,
        Guid deciderUserId,
        string? comment,
        bool reject);
}

/// <summary>The result of applying one decision.</summary>
public sealed record ApprovalAdvance(
    ApprovalAdvanceOutcome Outcome,
    short DecidedStepNo);

public enum ApprovalAdvanceOutcome
{
    /// <summary>The chain has more pending steps; voucher stays Submitted.</summary>
    StillInApproval = 0,

    /// <summary>Every step is now Approved; voucher transitions to Approved.</summary>
    FullyApproved = 1,

    /// <summary>The current step was Rejected; voucher transitions to Rejected.</summary>
    Rejected = 2
}
