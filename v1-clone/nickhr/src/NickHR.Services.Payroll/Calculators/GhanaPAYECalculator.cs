using NickHR.Services.Payroll.Models;

namespace NickHR.Services.Payroll.Calculators;

/// <summary>
/// Ghana Revenue Authority (GRA) Pay-As-You-Earn (PAYE) tax calculator.
/// Implements progressive monthly tax brackets per Ghana Income Tax Act.
///
/// 2024/2025 Monthly PAYE Brackets:
///   First  GHS    490  →  0%
///   Next   GHS    110  →  5%
///   Next   GHS    130  → 10%
///   Next   GHS  3,170  → 17.5%
///   Next   GHS 16,000  → 25%
///   Next   GHS 30,100  → 30%
///   Above  GHS 50,000  → 35%
///
/// Non-residents: flat 25%
/// </summary>
public static class GhanaPAYECalculator
{
    // Monthly tax brackets (cumulative upper bounds and marginal rates)
    private static readonly (decimal CumulativeLimit, decimal Rate, string Label)[] MonthlyBrackets =
    {
        (490m,      0.000m,  "First GHS 490"),
        (600m,      0.050m,  "Next GHS 110"),
        (730m,      0.100m,  "Next GHS 130"),
        (3_900m,    0.175m,  "Next GHS 3,170"),
        (19_900m,   0.250m,  "Next GHS 16,000"),
        (50_000m,   0.300m,  "Next GHS 30,100"),
        (decimal.MaxValue, 0.350m, "Above GHS 50,000"),
    };

    private const decimal NonResidentFlatRate = 0.25m;

    /// <summary>
    /// Calculate monthly PAYE tax for a resident individual.
    /// </summary>
    /// <param name="monthlyTaxableIncome">Taxable income after SSNIT and exempt allowances.</param>
    /// <param name="isResident">If false, applies flat 25% non-resident rate.</param>
    /// <returns>Monthly PAYE amount and bracket breakdown.</returns>
    public static (decimal Tax, List<TaxBracketBreakdown> Breakdown) Calculate(
        decimal monthlyTaxableIncome, bool isResident = true)
    {
        if (monthlyTaxableIncome <= 0)
            return (0m, new List<TaxBracketBreakdown>());

        if (!isResident)
        {
            var nonResidentTax = Math.Round(monthlyTaxableIncome * NonResidentFlatRate, 2);
            return (nonResidentTax, new List<TaxBracketBreakdown>
            {
                new()
                {
                    Bracket = "Non-Resident (Flat 25%)",
                    TaxableAmount = monthlyTaxableIncome,
                    Rate = NonResidentFlatRate * 100,
                    Tax = nonResidentTax
                }
            });
        }

        var breakdown = new List<TaxBracketBreakdown>();
        decimal totalTax = 0m;
        decimal previousLimit = 0m;

        foreach (var (cumulativeLimit, rate, label) in MonthlyBrackets)
        {
            if (monthlyTaxableIncome <= previousLimit)
                break;

            decimal bracketWidth = Math.Min(cumulativeLimit, monthlyTaxableIncome) - previousLimit;
            if (bracketWidth <= 0)
            {
                previousLimit = cumulativeLimit;
                continue;
            }

            decimal bracketTax = Math.Round(bracketWidth * rate, 2);
            totalTax += bracketTax;

            breakdown.Add(new TaxBracketBreakdown
            {
                Bracket = label,
                TaxableAmount = bracketWidth,
                Rate = rate * 100,
                Tax = bracketTax
            });

            previousLimit = cumulativeLimit;
        }

        return (Math.Round(totalTax, 2), breakdown);
    }

    /// <summary>
    /// Calculate annual PAYE by annualizing the monthly calculation.
    /// </summary>
    public static decimal CalculateAnnual(decimal annualTaxableIncome, bool isResident = true)
    {
        var monthlyTaxable = annualTaxableIncome / 12m;
        var (monthlyTax, _) = Calculate(monthlyTaxable, isResident);
        return Math.Round(monthlyTax * 12m, 2);
    }

    /// <summary>
    /// Calculate overtime tax for junior staff (earning <= GHS 18,000/year).
    /// Overtime up to 50% of monthly basic: taxed at 5%
    /// Overtime exceeding 50% of monthly basic: taxed at 10%
    /// </summary>
    public static decimal CalculateOvertimeTax(decimal overtimePay, decimal monthlyBasicSalary, decimal annualBasicSalary)
    {
        if (overtimePay <= 0) return 0m;

        // Only special overtime tax applies to junior staff
        if (annualBasicSalary > 18_000m)
            return 0m; // Regular PAYE applies for senior staff

        decimal threshold = monthlyBasicSalary * 0.5m;
        decimal taxAt5 = Math.Min(overtimePay, threshold) * 0.05m;
        decimal taxAt10 = overtimePay > threshold ? (overtimePay - threshold) * 0.10m : 0m;

        return Math.Round(taxAt5 + taxAt10, 2);
    }
}
