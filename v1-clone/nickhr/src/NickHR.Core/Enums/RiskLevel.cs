namespace NickHR.Core.Enums;

/// <summary>
/// Turnover/attrition risk band. String-equivalent ("Low", "Medium", "High") via
/// <c>nameof(RiskLevel.X)</c> so DTOs & UI labels stay stable.
/// </summary>
public enum RiskLevel
{
    Low,
    Medium,
    High
}
