namespace NickHR.Core.Constants;

/// <summary>
/// Domain-specific salary-component names that appear as literal strings on
/// <c>PayrollItemDetail</c> rows. Centralised here so spelling/spacing changes
/// don't quietly break statutory deduction grouping.
/// </summary>
public static class PayrollComponents
{
    /// <summary>Employee-side SSNIT contribution as it lands in PayrollItemDetail.ComponentName.</summary>
    public const string SsnitEmployeeName = "SSNIT Employee (5.5%)";

    /// <summary>PAYE tax row name as it lands in PayrollItemDetail.ComponentName.</summary>
    public const string PayeTaxName = "PAYE Tax";
}
