namespace NickHR.Core.Enums;

/// <summary>
/// Status of a timesheet entry. Persisted as string in <c>TimesheetEntry.Status</c>;
/// use <c>nameof(TimesheetStatus.X)</c> when reading/writing to keep on-disk values stable.
/// </summary>
public enum TimesheetStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected
}
