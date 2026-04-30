namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// Default engine — one step, any approver, just enforces "approver isn't
/// the requester". Matches the original MVP-zero behaviour so the existing
/// <c>PettyCashServiceTests</c> still pass without any changes.
/// </summary>
public sealed class SingleStepApprovalEngine : IApprovalEngine
{
    private readonly TimeProvider _clock;

    public SingleStepApprovalEngine(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public IReadOnlyList<VoucherApproval> Plan(Voucher voucher)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        return new[]
        {
            new VoucherApproval
            {
                VoucherId = voucher.VoucherId,
                StepNo = 1,
                Role = "any",
                AssignedToUserId = Guid.Empty,   // any user
                Decision = ApprovalDecision.Pending,
                CreatedAt = _clock.GetUtcNow(),
                TenantId = voucher.TenantId
            }
        };
    }

    public ApprovalAdvance Decide(
        Voucher voucher,
        IReadOnlyList<VoucherApproval> steps,
        Guid deciderUserId,
        string? comment,
        bool reject)
    {
        ArgumentNullException.ThrowIfNull(voucher);
        ArgumentNullException.ThrowIfNull(steps);

        if (deciderUserId == voucher.RequesterUserId)
        {
            throw new SeparationOfDutiesException("The requester cannot approve their own voucher.");
        }

        var step = steps.FirstOrDefault(s => s.Decision == ApprovalDecision.Pending)
            ?? throw new PettyCashException("No pending approval step found.");

        step.DecidedByUserId = deciderUserId;
        step.Comment = comment;
        step.DecidedAt = _clock.GetUtcNow();
        step.Decision = reject ? ApprovalDecision.Rejected : ApprovalDecision.Approved;

        return new ApprovalAdvance(
            reject ? ApprovalAdvanceOutcome.Rejected : ApprovalAdvanceOutcome.FullyApproved,
            step.StepNo);
    }
}
