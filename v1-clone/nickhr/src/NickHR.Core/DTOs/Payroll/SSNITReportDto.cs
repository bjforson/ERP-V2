namespace NickHR.Core.DTOs.Payroll;

public class SSNITReportDto
{
    public int Month { get; set; }
    public int Year { get; set; }
    public List<SSNITReportEntryDto> Entries { get; set; } = new();
    public decimal TotalEmployeeContribution { get; set; }
    public decimal TotalEmployerContribution { get; set; }
    public decimal TotalContribution { get; set; }
}
