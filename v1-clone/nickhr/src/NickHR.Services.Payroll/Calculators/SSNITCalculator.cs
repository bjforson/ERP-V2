namespace NickHR.Services.Payroll.Calculators;

/// <summary>
/// Social Security and National Insurance Trust (SSNIT) contribution calculator.
///
/// Tier 1 (SSNIT Basic):
///   Employee contribution: 5.5% of basic salary
///   Employer contribution: 13% of basic salary
///   Total: 18.5% of basic salary
///
/// Notes:
///   - Contributions are based on BASIC salary only (not total earnings)
///   - Maximum insurable earnings for 2025-26: GHS 61,000/month
///   - SSNIT deduction is exempt from income tax (reduces taxable income)
/// </summary>
public static class SSNITCalculator
{
    public const decimal EmployeeRate = 0.055m;     // 5.5%
    public const decimal EmployerRate = 0.13m;      // 13%
    public const decimal TotalRate = 0.185m;        // 18.5%
    public const decimal MaxInsurableEarnings = 61_000m; // Monthly cap for 2025-26

    /// <summary>
    /// Calculate monthly SSNIT contributions based on basic salary.
    /// </summary>
    /// <param name="basicSalary">Monthly basic salary (not gross).</param>
    /// <returns>Employee contribution, Employer contribution, and the capped basic used.</returns>
    public static (decimal EmployeeContribution, decimal EmployerContribution, decimal CappedBasic) Calculate(decimal basicSalary)
    {
        if (basicSalary <= 0)
            return (0m, 0m, 0m);

        // Cap at maximum insurable earnings
        decimal cappedBasic = Math.Min(basicSalary, MaxInsurableEarnings);

        decimal employeeContribution = Math.Round(cappedBasic * EmployeeRate, 2);
        decimal employerContribution = Math.Round(cappedBasic * EmployerRate, 2);

        return (employeeContribution, employerContribution, cappedBasic);
    }

    /// <summary>
    /// Calculate the SSNIT-adjusted taxable income.
    /// Taxable Income = Gross Pay - Employee SSNIT Contribution - Non-taxable Allowances
    /// </summary>
    public static decimal CalculateTaxableIncome(decimal grossPay, decimal ssnitEmployeeContribution, decimal nonTaxableAllowances = 0m)
    {
        decimal taxable = grossPay - ssnitEmployeeContribution - nonTaxableAllowances;
        return Math.Max(taxable, 0m); // Cannot be negative
    }
}
