using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// Multi-step engine driven by an <see cref="ApprovalPolicy"/>. For each
/// voucher it picks the matching <see cref="ApprovalBand"/> by amount,
/// then resolves each band step's roles into users via
/// <see cref="IApproverResolver"/>. Sequential approval — every step
/// must land Approved (parallel: every role row at that step) before
/// the next step opens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parallel approvers</b>: a step with multiple roles produces one
/// <see cref="VoucherApproval"/> row per role at the same
/// <c>step_no</c>. The step is "complete" when every row at that
/// <c>step_no</c> is <see cref="ApprovalDecision.Approved"/>; the
/// chain advances when the highest-numbered Pending step has been
/// fully approved.
/// </para>
/// <para>
/// <b>Delegation</b>: callers wrap the resolver with
/// <see cref="DelegatingApproverResolver"/> which substitutes a
/// covering user when the assignee has an active row in
/// <c>petty_cash.approval_delegations</c>. Audit captures the
/// original-vs-actual via the engine's
/// <see cref="VoucherApproval.Comment"/> on decide.
/// </para>
/// <para>
/// <b>Escalation</b> at decision time (i.e. when an operator runs the
/// scheduled escalator) is handled by
/// <see cref="IPettyCashService.EscalateOverdueApprovalsAsync"/> —
/// the engine itself stays stateless about wall-clock.
/// </para>
/// <para>
/// <b>Out-of-band notify</b>: the engine fires
/// <see cref="IApprovalNotifier.NotifyAsync"/> for vouchers at-or-above
/// <see cref="_notifyThresholdMinor"/> as a side effect of <see cref="Plan"/>.
/// The on-screen approval queue is the source of truth — notify failures
/// are logged and swallowed; <see cref="Plan"/> never throws because of a
/// notifier hiccup. The default threshold is GHS 100,000.00; the host can
/// pass a smaller value (e.g. for AP payment runs) via the
/// constructor.
/// </para>
/// </remarks>
public sealed class PolicyApprovalEngine : IApprovalEngine
{
    private readonly ApprovalPolicy _policy;
    private readonly IApproverResolver _resolver;
    private readonly TimeProvider _clock;
    private readonly IApprovalNotifier _notifier;
    private readonly IApproverPhoneResolver _phoneResolver;
    private readonly ILogger<PolicyApprovalEngine> _logger;
    private readonly long _notifyThresholdMinor;

    public PolicyApprovalEngine(
        ApprovalPolicy policy,
        IApproverResolver resolver,
        TimeProvider? clock = null)
        : this(policy, resolver, clock,
            new NoopApprovalNotifier(),
            new NoopApproverPhoneResolver(),
            NullLogger<PolicyApprovalEngine>.Instance,
            10_000_000L)
    {
    }

    /// <summary>
    /// Notifier-aware overload. <paramref name="notifyThresholdMinor"/>
    /// defaults to <c>10_000_000</c> (GHS 100,000.00) — vouchers at-or-above
    /// that figure trigger an out-of-band notify on each step's resolved
    /// approver. Pass a smaller value to widen the net.
    /// </summary>
    public PolicyApprovalEngine(
        ApprovalPolicy policy,
        IApproverResolver resolver,
        TimeProvider? clock,
        IApprovalNotifier notifier,
        IApproverPhoneResolver phoneResolver,
        ILogger<PolicyApprovalEngine> logger,
        long notifyThresholdMinor = 10_000_000L)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _clock = clock ?? TimeProvider.System;
        _notifier = notifier ?? new NoopApprovalNotifier();
        _phoneResolver = phoneResolver ?? new NoopApproverPhoneResolver();
        _logger = logger ?? NullLogger<PolicyApprovalEngine>.Instance;
        _notifyThresholdMinor = notifyThresholdMinor;
    }

    public IReadOnlyList<VoucherApproval> Plan(Voucher voucher)
    {
        ArgumentNullException.ThrowIfNull(voucher);

        var band = _policy.BandFor(voucher.Category, voucher.AmountRequestedMinor)
            ?? throw new PettyCashException(
                $"No approval band covers a {voucher.AmountRequestedMinor}-minor "
                + $"{voucher.Category} voucher under policy version {_policy.Version}. "
                + "Either the amount exceeds the largest band or the category isn't policy-mapped.");

        var now = _clock.GetUtcNow();
        var rows = new List<VoucherApproval>(band.Steps.Count);
        short stepNo = 1;
        foreach (var step in band.Steps)
        {
            // Each role within a step becomes its own row at the same step_no.
            // For parallel steps, all rows must Approve before the step is done.
            // For solo steps, there's just one row at this step_no.
            var assignedAny = false;
            foreach (var role in step.Roles)
            {
                var assignee = _resolver.Resolve(role, voucher);
                var unfillable = assignee == Guid.Empty || assignee == voucher.RequesterUserId;
                rows.Add(new VoucherApproval
                {
                    VoucherId = voucher.VoucherId,
                    StepNo = stepNo,
                    Role = role,
                    AssignedToUserId = unfillable ? Guid.Empty : assignee,
                    Decision = unfillable ? ApprovalDecision.UnfillableRole : ApprovalDecision.Pending,
                    CreatedAt = now,
                    TenantId = voucher.TenantId
                });
                if (!unfillable) assignedAny = true;
            }
            if (!assignedAny)
            {
                // None of the parallel roles could be filled — short-circuit so
                // the consumer service can mark the voucher Rejected up-front.
            }
            stepNo++;
        }

        // OUT-OF-BAND NOTIFY: high-value vouchers wake their approvers via
        // WhatsApp (or whatever channel the host wired). Failures are
        // swallowed — the on-screen approval queue is the source of truth.
        if (voucher.AmountRequestedMinor >= _notifyThresholdMinor)
        {
            foreach (var row in rows)
            {
                if (row.Decision != ApprovalDecision.Pending) continue;
                if (row.AssignedToUserId == Guid.Empty) continue;

                string? phone = null;
                try
                {
                    phone = _phoneResolver.ResolvePhoneByUserIdAsync(row.AssignedToUserId).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Phone resolve failed for approver {UserId} on voucher {VoucherNo}", row.AssignedToUserId, voucher.VoucherNo);
                }
                if (string.IsNullOrWhiteSpace(phone)) continue;

                var msg = new ApprovalNotification(
                    VoucherId: voucher.VoucherId,
                    VoucherNo: voucher.VoucherNo,
                    AmountMinor: voucher.AmountRequestedMinor,
                    CurrencyCode: voucher.CurrencyCode,
                    ApproverIdentifier: phone,
                    Purpose: voucher.Purpose,
                    TenantId: voucher.TenantId);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _notifier.NotifyAsync(msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "WhatsApp notify failed for voucher {VoucherNo}", voucher.VoucherNo);
                    }
                });
            }
        }

        return rows;
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

        if (steps.Any(s => s.Decision == ApprovalDecision.UnfillableRole))
        {
            throw new PettyCashException("Voucher has an unfillable approval step; cannot decide.");
        }

        // Find the lowest step_no that still has any Pending row.
        var pendingStepNo = steps
            .Where(s => s.Decision == ApprovalDecision.Pending)
            .Select(s => (int)s.StepNo)
            .DefaultIfEmpty(int.MinValue)
            .Min();
        if (pendingStepNo == int.MinValue)
        {
            throw new PettyCashException("No pending approval step found.");
        }

        // Among rows at that step, find the one assigned to the decider.
        // (Decider could be the original assignee, an escalated assignee, or
        // a delegated covering user — the resolver wraps that complexity.)
        var row = steps.FirstOrDefault(s =>
            s.StepNo == pendingStepNo &&
            s.Decision == ApprovalDecision.Pending &&
            s.AssignedToUserId == deciderUserId);
        if (row is null)
        {
            throw new SeparationOfDutiesException(
                $"User {deciderUserId} is not an assigned approver on the open step (step {pendingStepNo}) of voucher {voucher.VoucherId}.");
        }
        if (deciderUserId == voucher.RequesterUserId)
        {
            throw new SeparationOfDutiesException("The requester cannot decide on their own voucher.");
        }

        row.DecidedByUserId = deciderUserId;
        row.Comment = comment;
        row.DecidedAt = _clock.GetUtcNow();
        row.Decision = reject ? ApprovalDecision.Rejected : ApprovalDecision.Approved;

        if (reject)
        {
            return new ApprovalAdvance(ApprovalAdvanceOutcome.Rejected, row.StepNo);
        }

        // ESCALATION SHORT-CIRCUIT: when an escalation row exists at the same
        // step_no AND a different role from the row that just approved, the
        // approval clears the role's quorum on its own. We mark all OTHER
        // still-Pending rows for the same role at this step as Skipped (they
        // were the same "slot" — escalations are an OR with the original).
        // Pure parallel approvers (different roles each, no escalation) keep
        // their AND semantic because their roles differ from the row that
        // just approved.
        var sameRoleAfterTrim = SameRoleNormalised(row.Role);
        foreach (var other in steps.Where(s =>
            s.StepNo == pendingStepNo &&
            s.Decision == ApprovalDecision.Pending &&
            SameRoleNormalised(s.Role) == sameRoleAfterTrim))
        {
            other.Decision = ApprovalDecision.Skipped;
            other.DecidedAt = _clock.GetUtcNow();
            other.Comment = $"Skipped — step decided by {row.Role}";
        }

        // Step done? — no Pending left at this step_no, no Rejected at this step_no,
        // and every distinct role at this step_no has at least one Approved row.
        var thisStep = steps.Where(s => s.StepNo == pendingStepNo).ToList();
        var anyPending = thisStep.Any(s => s.Decision == ApprovalDecision.Pending);
        if (anyPending)
        {
            return new ApprovalAdvance(ApprovalAdvanceOutcome.StillInApproval, row.StepNo);
        }
        var rolesAtStep = thisStep.Select(s => SameRoleNormalised(s.Role)).Distinct().ToList();
        var allRolesApproved = rolesAtStep.All(r =>
            thisStep.Any(s => SameRoleNormalised(s.Role) == r && s.Decision == ApprovalDecision.Approved));
        if (!allRolesApproved)
        {
            return new ApprovalAdvance(ApprovalAdvanceOutcome.StillInApproval, row.StepNo);
        }

        var anyLaterPending = steps
            .Any(s => s.StepNo > pendingStepNo && s.Decision == ApprovalDecision.Pending);
        return new ApprovalAdvance(
            anyLaterPending ? ApprovalAdvanceOutcome.StillInApproval : ApprovalAdvanceOutcome.FullyApproved,
            row.StepNo);
    }

    /// <summary>
    /// Strip the " (escalated)" suffix added by
    /// <see cref="IPettyCashService.EscalateOverdueApprovalsAsync"/> so the
    /// engine treats an escalation row and its original as the same role
    /// "slot" for quorum purposes.
    /// </summary>
    private static string SameRoleNormalised(string role)
    {
        const string Suffix = " (escalated)";
        return role.EndsWith(Suffix, StringComparison.Ordinal)
            ? role[..^Suffix.Length]
            : role;
    }
}
