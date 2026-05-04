using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class SuccessionCandidate : BaseEntity
{
    public int SuccessionPlanId { get; set; }

    public int CandidateEmployeeId { get; set; }

    [MaxLength(20)]
    public string ReadinessLevel { get; set; } = "1to2Years"; // ReadyNow, 1to2Years, 3PlusYears

    [MaxLength(1000)]
    public string? DevelopmentNeeds { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation
    public SuccessionPlan SuccessionPlan { get; set; } = null!;
    public Employee CandidateEmployee { get; set; } = null!;
}
