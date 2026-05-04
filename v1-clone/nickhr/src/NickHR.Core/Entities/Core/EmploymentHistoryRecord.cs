using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class EmploymentHistoryRecord : BaseEntity
{
    public int EmployeeId { get; set; }

    [Required]
    [MaxLength(300)]
    public string CompanyName { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string JobTitle { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [MaxLength(500)]
    public string? ReasonForLeaving { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;
}
