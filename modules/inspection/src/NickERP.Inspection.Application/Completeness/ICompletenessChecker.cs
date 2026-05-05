using NickERP.Inspection.Core.Completeness;

namespace NickERP.Inspection.Application.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — completeness-rollup engine contract. Lives in
/// <c>Application/Completeness</c> so the
/// <c>NickERP.Inspection.Web.Services.CaseWorkflowService</c> can call
/// it inline at state transitions without a hard reference on the
/// <see cref="CompletenessChecker"/> concrete (eases test wiring).
/// </summary>
public interface ICompletenessChecker
{
    /// <summary>
    /// Evaluate every registered + tenant-enabled
    /// <see cref="ICompletenessRequirement"/> against the case and
    /// persist outcomes as Findings + audit events. Returns the
    /// aggregated result so callers can branch on
    /// <see cref="CompletenessEvaluationResult.RollupSeverity"/>.
    /// </summary>
    Task<CompletenessEvaluationResult> EvaluateAsync(Guid caseId, CancellationToken ct = default);
}
