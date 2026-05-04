using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Performance;

public class CompetencyFramework : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public int? DesignationId { get; set; }

    public int? GradeId { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation Properties
    public Designation? Designation { get; set; }

    public Grade? Grade { get; set; }

    public ICollection<Competency> Competencies { get; set; } = new List<Competency>();
}
