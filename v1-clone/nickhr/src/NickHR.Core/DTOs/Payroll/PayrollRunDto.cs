using NickHR.Core.Enums;

namespace NickHR.Core.DTOs.Payroll;

public class PayrollRunDto
{
    public int Id { get; set; }
    public int PayPeriodMonth { get; set; }
    public int PayPeriodYear { get; set; }
    public DateTime RunDate { get; set; }
    public PayrollStatus Status { get; set; }
    public decimal TotalGross { get; set; }
    public decimal TotalNet { get; set; }
    public decimal TotalSSNIT { get; set; }
    public decimal TotalPAYE { get; set; }
    public int EmployeeCount { get; set; }
    public string? ProcessedBy { get; set; }
}
