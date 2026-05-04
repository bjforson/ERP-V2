namespace NickHR.Core.DTOs.Payroll;

public class PayslipDto
{
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string? Designation { get; set; }
    public decimal BasicSalary { get; set; }
    public List<PayslipLineDto> Earnings { get; set; } = new();
    public List<PayslipLineDto> Deductions { get; set; } = new();
    public decimal GrossPay { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal NetPay { get; set; }

    /// <summary>Employee SSNIT contribution (5.5% of basic salary)</summary>
    public decimal SSNITEmployee { get; set; }

    /// <summary>Employer SSNIT contribution (13% of basic salary)</summary>
    public decimal SSNITEmployer { get; set; }

    public decimal PAYE { get; set; }

    // Year-to-date figures
    public decimal YTDGross { get; set; }
    public decimal YTDNet { get; set; }
    public decimal YTDTax { get; set; }
}
