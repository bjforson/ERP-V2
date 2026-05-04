using NickHR.Core.Enums;

namespace NickHR.Core.DTOs.Employee;

public class EmployeeSearchFilter
{
    public string? SearchTerm { get; set; }
    public int? DepartmentId { get; set; }
    public int? GradeId { get; set; }
    public int? LocationId { get; set; }
    public EmploymentStatus? EmploymentStatus { get; set; }
    public EmploymentType? EmploymentType { get; set; }
    public DateTime? HireFromDate { get; set; }
    public DateTime? HireToDate { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? SortBy { get; set; }
    public string? SortDirection { get; set; }
}
