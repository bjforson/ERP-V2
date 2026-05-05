using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Application;

/// <summary>
/// Sprint 48 / Phase A — FU-strict-mode-block-on-error.
///
/// <para>
/// Thrown by <c>CaseWorkflowService.SubmitAsync</c> when the per-tenant
/// validation strict-mode flag is enabled
/// (<c>inspection.validation.strict_mode_enabled</c> via Sprint 35
/// <c>ITenantSettingsService</c>) AND the most-recent
/// <see cref="ValidationEngineResult"/> for the case carried at least
/// one <see cref="ValidationSeverity.Error"/> outcome.
/// </para>
///
/// <para>
/// <b>Not a 4xx HTTP exception.</b> The Web layer's submission page handler
/// is expected to catch this and surface a meaningful UX message to the
/// analyst (e.g. "Submission blocked — N validation errors must be
/// resolved first"). Mapping it to a generic 500 / 4xx in the global
/// error filter would obscure the recoverable nature of the gate.
/// </para>
///
/// <para>
/// <see cref="CaseId"/> + <see cref="ErrorCount"/> + <see cref="FailingRuleIds"/>
/// land in the message and as structured properties so the caller can
/// render the failing rule list without re-running the engine. The list
/// is bounded to the actual error-severity outcomes from the engine
/// snapshot (Warning + Info do NOT appear here — only Error blocks).
/// </para>
/// </summary>
public sealed class ValidationStrictModeException : Exception
{
    /// <summary>The case whose submission was blocked.</summary>
    public Guid CaseId { get; }

    /// <summary>Count of error-severity outcomes from the engine snapshot.</summary>
    public int ErrorCount { get; }

    /// <summary>
    /// Stable rule ids of the rules that emitted Error-severity outcomes.
    /// Ordered as they appeared in the engine result (which is sorted by
    /// rule id ascending, so this is alphabetical and stable).
    /// </summary>
    public IReadOnlyList<string> FailingRuleIds { get; }

    public ValidationStrictModeException(
        Guid caseId,
        int errorCount,
        IReadOnlyList<string> failingRuleIds)
        : base(BuildMessage(caseId, errorCount, failingRuleIds))
    {
        CaseId = caseId;
        ErrorCount = errorCount;
        FailingRuleIds = failingRuleIds ?? Array.Empty<string>();
    }

    private static string BuildMessage(Guid caseId, int errorCount, IReadOnlyList<string> failingRuleIds)
    {
        var ruleList = failingRuleIds is { Count: > 0 }
            ? string.Join(", ", failingRuleIds)
            : "(none recorded)";
        return $"Submission blocked by validation strict mode for case {caseId}: "
               + $"{errorCount} validation error(s) must be resolved first. "
               + $"Failing rules: {ruleList}.";
    }
}
