using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Performance;

public class Goal : BaseEntity
{
    public int EmployeeId { get; set; }

    public int AppraisalCycleId { get; set; }

    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? TargetValue { get; set; }

    [MaxLength(200)]
    public string? AchievedValue { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal Weight { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal ProgressPercent { get; set; }

    public DateTime? DueDate { get; set; }

    /// <summary>NotStarted, InProgress, or Completed</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "NotStarted";

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public AppraisalCycle AppraisalCycle { get; set; } = null!;
}
