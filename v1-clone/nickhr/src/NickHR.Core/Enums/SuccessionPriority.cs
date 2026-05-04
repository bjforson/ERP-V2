namespace NickHR.Core.Enums;

/// <summary>
/// Succession-plan priority. Explicit values give natural sort order
/// (Critical first, Low last) so callers can simply <c>OrderBy((int)p)</c>.
/// String form (<c>nameof(SuccessionPriority.X)</c>) is what's persisted in
/// <c>SuccessionPlan.Priority</c>.
/// </summary>
public enum SuccessionPriority
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3
}
