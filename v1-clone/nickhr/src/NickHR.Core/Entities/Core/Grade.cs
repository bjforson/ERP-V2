using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Core;

public class Grade : BaseEntity
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int Level { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MinSalary { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MidSalary { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MaxSalary { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation Properties
    public ICollection<Designation> Designations { get; set; } = new List<Designation>();

    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}
