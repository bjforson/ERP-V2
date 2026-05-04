using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Training;

public class TrainingProgram : BaseEntity
{
    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    [MaxLength(300)]
    public string? Provider { get; set; }

    [MaxLength(300)]
    public string? Location { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public int MaxParticipants { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Cost { get; set; }

    /// <summary>Internal or External</summary>
    [MaxLength(20)]
    public string TrainingType { get; set; } = "Internal";

    public int? DepartmentId { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation Properties
    public Department? Department { get; set; }

    public ICollection<TrainingAttendance> TrainingAttendances { get; set; } = new List<TrainingAttendance>();
}
