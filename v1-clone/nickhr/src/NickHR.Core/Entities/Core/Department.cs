using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class Department : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public int? ParentDepartmentId { get; set; }

    [MaxLength(50)]
    public string? CostCenter { get; set; }

    public bool IsActive { get; set; } = true;

    public int? HeadOfDepartmentId { get; set; }

    // Navigation Properties
    public Department? ParentDepartment { get; set; }

    public ICollection<Department> SubDepartments { get; set; } = new List<Department>();

    public Employee? HeadOfDepartment { get; set; }

    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}
