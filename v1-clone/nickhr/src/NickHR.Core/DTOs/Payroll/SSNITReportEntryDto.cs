namespace NickHR.Core.DTOs.Payroll;

public class SSNITReportEntryDto
{
    public string EmployeeSSNITNumber { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public decimal BasicSalary { get; set; }

    /// <summary>Employee contribution at 5.5% of basic salary</summary>
    public decimal EmployeeContribution { get; set; }

    /// <summary>Employer contribution at 13% of basic salary</summary>
    public decimal EmployerContribution { get; set; }
}
