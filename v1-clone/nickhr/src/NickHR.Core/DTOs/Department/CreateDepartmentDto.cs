using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.DTOs.Department;

public class CreateDepartmentDto
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public int? ParentDepartmentId { get; set; }

    [MaxLength(100)]
    public string? CostCenter { get; set; }

    public int? HeadOfDepartmentId { get; set; }
}
