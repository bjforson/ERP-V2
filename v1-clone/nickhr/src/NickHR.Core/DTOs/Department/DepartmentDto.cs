namespace NickHR.Core.DTOs.Department;

public class DepartmentDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? ParentDepartmentId { get; set; }
    public string? ParentDepartmentName { get; set; }
    public string? CostCenter { get; set; }
    public int? HeadOfDepartmentId { get; set; }
    public string? HeadOfDepartmentName { get; set; }
    public int EmployeeCount { get; set; }
    public bool IsActive { get; set; }
}
