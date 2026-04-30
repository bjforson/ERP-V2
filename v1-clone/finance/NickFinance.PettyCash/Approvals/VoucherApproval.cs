namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// One step in a voucher's approval chain. <see cref="StepNo"/> is 1-based
/// and ordered. The engine creates pending rows on submit and updates
/// them on each decision; the voucher transitions to <c>Approved</c> when
/// every step has <see cref="ApprovalDecision.Approved"/>, or jumps
/// straight to <c>Rejected</c> on the first <see cref="ApprovalDecision.Rejected"/>.
/// </summary>
public class VoucherApproval
{
    public Guid VoucherApprovalId { get; set; } = Guid.NewGuid();

    public Guid VoucherId { get; set; }

    /// <summary>1-based step ordinal within the voucher.</summary>
    public short StepNo { get; set; }

    /// <summary>The role that this step represents (e.g. <c>line_manager</c>). Snapshot from the policy at submit time.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>The user expected to make this decision. Resolved at submit; nulled if no one fills the role.</summary>
    public Guid AssignedToUserId { get; set; }

    /// <summary>Current decision state.</summary>
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;

    /// <summary>The user who actually decided. Usually equal to <see cref="AssignedToUserId"/>; differs when delegation arrives in v1.2.</summary>
    public Guid? DecidedByUserId { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public long TenantId { get; set; } = 1;
}

/// <summary>State of one step within a voucher's approval chain.</summary>
public enum ApprovalDecision
{
    /// <summary>Awaiting a decision from <see cref="VoucherApproval.AssignedToUserId"/>.</summary>
    Pending = 0,

    /// <summary>Approved — chain advances to the next step (or finalises the voucher).</summary>
    Approved = 1,

    /// <summary>Rejected — voucher transitions to <c>Rejected</c> immediately.</summary>
    Rejected = 2,

    /// <summary>The role couldn't be filled at submit time; voucher is auto-rejected. Recorded for audit.</summary>
    UnfillableRole = 3,

    /// <summary>
    /// The row was rendered moot by another decision on the same step
    /// (e.g. an escalation row got the Approved before the original
    /// assignee saw it, or vice versa). Audit-only state.
    /// </summary>
    Skipped = 4
}
