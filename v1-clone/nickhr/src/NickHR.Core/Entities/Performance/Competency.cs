using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Performance;

public class Competency : BaseEntity
{
    public int CompetencyFrameworkId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal Weight { get; set; }

    // Navigation Properties
    public CompetencyFramework CompetencyFramework { get; set; } = null!;
}
