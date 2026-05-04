using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class SuccessionPlan : BaseEntity
{
    public int DesignationId { get; set; }

    public int? IncumbentEmployeeId { get; set; }

    [MaxLength(20)]
    public string Priority { get; set; } = "Medium"; // Critical, High, Medium, Low

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation
    public Designation Designation { get; set; } = null!;
    public Employee? IncumbentEmployee { get; set; }
    public ICollection<SuccessionCandidate> Candidates { get; set; } = new List<SuccessionCandidate>();
}
