namespace NickHR.Core.Enums;

/// <summary>
/// Status of a probation review row in the workflow.
/// Persisted as string in <c>ProbationReview.Status</c>; compare/assign via
/// <c>nameof(ProbationStatus.X)</c> or <c>.ToString()</c> to keep on-disk values stable.
/// </summary>
public enum ProbationStatus
{
    Pending,
    Completed,
    Extended,
    Failed
}
