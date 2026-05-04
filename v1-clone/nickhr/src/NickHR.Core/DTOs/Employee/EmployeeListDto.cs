using NickHR.Core.Enums;

namespace NickHR.Core.DTOs.Employee;

public class EmployeeListDto
{
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Department { get; set; }
    public string? Designation { get; set; }
    public string? Grade { get; set; }
    public EmploymentStatus EmploymentStatus { get; set; }
    public DateTime? HireDate { get; set; }
    public string? PhotoUrl { get; set; }
}
