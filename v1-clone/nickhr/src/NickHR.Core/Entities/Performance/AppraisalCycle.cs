using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Performance;

public class AppraisalCycle : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation Properties
    public ICollection<AppraisalForm> AppraisalForms { get; set; } = new List<AppraisalForm>();
}
