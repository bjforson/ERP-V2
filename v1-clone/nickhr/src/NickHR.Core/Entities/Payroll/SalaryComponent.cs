using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class SalaryComponent : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    public SalaryComponentType ComponentType { get; set; }

    public bool IsFixed { get; set; }

    public bool IsTaxable { get; set; }

    public bool IsStatutory { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? DefaultAmount { get; set; }

    [Column(TypeName = "decimal(10,4)")]
    public decimal? DefaultPercentage { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}
