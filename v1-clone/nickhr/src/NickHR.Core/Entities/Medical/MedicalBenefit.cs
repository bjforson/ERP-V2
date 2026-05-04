using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Medical;

public class MedicalBenefit : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal AnnualLimit { get; set; }

    // JSON string for category-specific limits e.g. {"Consultation": 500, "Surgery": 5000}
    [MaxLength(2000)]
    public string? CategoryLimits { get; set; }

    public int WaitingPeriodMonths { get; set; }

    public bool CoversDependents { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}
