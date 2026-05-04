using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class ProbationReview : BaseEntity
{
    public int EmployeeId { get; set; }

    public DateTime ReviewDate { get; set; }

    public DateTime ProbationEndDate { get; set; }

    public ProbationDecision Decision { get; set; }

    public int? ExtensionMonths { get; set; }

    public DateTime? NewProbationEndDate { get; set; }

    [MaxLength(2000)]
    public string? ManagerComments { get; set; }

    [MaxLength(2000)]
    public string? HRComments { get; set; }

    public int ReviewedById { get; set; }

    public DateTime? ReviewedAt { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Pending";

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public Employee ReviewedBy { get; set; } = null!;
}
