namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// In-memory representation of the Petty Cash approval policy. Built either
/// programmatically (tests, simple consumers) or by parsing the YAML DSL
/// in <see cref="ApprovalPolicyYamlLoader"/>.
/// </summary>
/// <remarks>
/// <para>
/// The policy is keyed by <see cref="VoucherCategory"/>. Each category has
/// a list of <see cref="ApprovalBand"/> rows ordered by ascending
/// <see cref="ApprovalBand.MaxAmountMinor"/>. The first band whose
/// <c>MaxAmountMinor</c> meets or exceeds the voucher amount is the
/// matching band; its <see cref="ApprovalBand.Steps"/> are the sequential
/// approval roles needed.
/// </para>
/// <para>
/// Roles are case-insensitive identifier strings (<c>line_manager</c>,
/// <c>site_supervisor</c>, <c>finance</c>, …); the host resolves a role
/// + voucher into an actual user id via <see cref="IApproverResolver"/>.
/// </para>
/// </remarks>
public sealed record ApprovalPolicy(
    string Version,
    IReadOnlyDictionary<VoucherCategory, IReadOnlyList<ApprovalBand>> Categories)
{
    /// <summary>
    /// Find the band that matches a voucher's amount. Returns
    /// <see langword="null"/> when the category has no policy or no band
    /// covers the amount — caller decides whether that's "auto-approve",
    /// "fail loudly", or "fall back to single-step".
    /// </summary>
    public ApprovalBand? BandFor(VoucherCategory category, long amountMinor)
    {
        if (!Categories.TryGetValue(category, out var bands)) return null;
        foreach (var band in bands)
        {
            if (amountMinor <= band.MaxAmountMinor) return band;
        }
        return null;
    }
}

/// <summary>
/// One band row inside a <see cref="ApprovalPolicy"/>. <see cref="Steps"/>
/// is a list of <see cref="ApprovalStep"/>; each step has one or more
/// roles that all must approve (parallel) before the chain advances.
/// </summary>
public sealed record ApprovalBand(
    long MaxAmountMinor,
    IReadOnlyList<ApprovalStep> Steps);

/// <summary>
/// One step within a band. A step has 1+ <see cref="Roles"/>; the engine
/// creates one <see cref="VoucherApproval"/> row per role at the same
/// <c>step_no</c>. The step is "complete" only once <em>every</em> role
/// at that step has decided Approved (parallel approval).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EscalateAfterHours"/> + <see cref="EscalateTo"/> drive the
/// timeout-based escalation. When a step is older than the threshold
/// without a decision, an operator (or scheduled job) calls
/// <see cref="IPettyCashService"/>.<c>EscalateOverdueApprovalsAsync</c>
/// which inserts an additional <see cref="VoucherApproval"/> row at the
/// same <c>step_no</c> assigned to <see cref="EscalateTo"/> resolved
/// against the original requester. The original row stays Pending —
/// either the original assignee or the escalation target can clear the
/// step.
/// </para>
/// </remarks>
public sealed record ApprovalStep(
    IReadOnlyList<string> Roles,
    int? EscalateAfterHours = null,
    string? EscalateTo = null);
