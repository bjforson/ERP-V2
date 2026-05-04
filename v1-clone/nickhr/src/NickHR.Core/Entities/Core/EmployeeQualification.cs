using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class EmployeeQualification : BaseEntity
{
    public int EmployeeId { get; set; }

    /// <summary>Education, Certification, or Language</summary>
    [Required]
    [MaxLength(50)]
    public string QualificationType { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Institution { get; set; }

    [Required]
    [MaxLength(300)]
    public string Qualification { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? FieldOfStudy { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [MaxLength(50)]
    public string? Grade { get; set; }

    public bool IsHighest { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;
}
