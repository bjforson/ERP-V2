using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class Designation : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public int? GradeId { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation Properties
    public Grade? Grade { get; set; }

    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}
