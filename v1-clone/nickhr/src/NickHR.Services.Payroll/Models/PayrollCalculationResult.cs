namespace NickHR.Services.Payroll.Models;

/// <summary>
/// Result of a complete payroll calculation for one employee for one month.
/// </summary>
public class PayrollCalculationResult
{
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;

    // Earnings
    public decimal BasicSalary { get; set; }
    public List<PayrollLineItem> Allowances { get; set; } = new();
    public decimal TotalAllowances => Allowances.Sum(a => a.Amount);
    public decimal OvertimeHours { get; set; }
    public decimal OvertimePay { get; set; }
    public decimal GrossPay => BasicSalary + TotalAllowances + OvertimePay;

    // Statutory Deductions
    public decimal SSNITEmployee { get; set; }     // 5.5% of basic
    public decimal SSNITEmployer { get; set; }     // 13% of basic
    public decimal TaxableIncome { get; set; }
    public decimal PAYE { get; set; }
    public List<TaxBracketBreakdown> TaxBreakdown { get; set; } = new();

    // Other Deductions
    public List<PayrollLineItem> Deductions { get; set; } = new();
    public decimal TotalOtherDeductions => Deductions.Sum(d => d.Amount);
    public decimal TotalDeductions => SSNITEmployee + PAYE + TotalOtherDeductions;

    // Net Pay
    public decimal NetPay => GrossPay - TotalDeductions;

    // Bank Details
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? MobileMoneyNumber { get; set; }

    // Validation
    public List<string> Warnings { get; set; } = new();
    public bool HasWarnings => Warnings.Any();
}

public class PayrollLineItem
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool IsTaxable { get; set; }
    public bool IsStatutory { get; set; }
}

public class TaxBracketBreakdown
{
    public string Bracket { get; set; } = string.Empty;
    public decimal TaxableAmount { get; set; }
    public decimal Rate { get; set; }
    public decimal Tax { get; set; }
}
