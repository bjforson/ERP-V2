using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Training;

public class EmployeeSkill : BaseEntity
{
    public int EmployeeId { get; set; }

    public int SkillId { get; set; }

    /// <summary>Proficiency level from 1 (Beginner) to 5 (Expert)</summary>
    [Range(1, 5)]
    public int ProficiencyLevel { get; set; }

    public DateTime? CertifiedDate { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public Skill Skill { get; set; } = null!;
}
