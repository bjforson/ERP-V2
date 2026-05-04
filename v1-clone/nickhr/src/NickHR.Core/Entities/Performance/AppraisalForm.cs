using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Performance;

public class AppraisalForm : BaseEntity
{
    public int AppraisalCycleId { get; set; }

    public int EmployeeId { get; set; }

    public int ReviewerId { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? SelfRating { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? ManagerRating { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? FinalRating { get; set; }

    [MaxLength(2000)]
    public string? SelfComments { get; set; }

    [MaxLength(2000)]
    public string? ManagerComments { get; set; }

    public AppraisalStatus Status { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? ReviewedAt { get; set; }

    // Navigation Properties
    public AppraisalCycle AppraisalCycle { get; set; } = null!;

    public Employee Employee { get; set; } = null!;

    public Employee Reviewer { get; set; } = null!;
}
