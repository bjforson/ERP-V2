using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Discipline;

public class Grievance : BaseEntity
{
    /// <summary>
    /// Employee who filed the grievance. Nullable to support anonymous grievances
    /// (PRIVACY: when IsAnonymous=true, this MUST be null so the row carries no
    /// linkable identifier back to the filer).
    /// </summary>
    public int? EmployeeId { get; set; }

    [Required]
    [MaxLength(300)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string Description { get; set; } = string.Empty;

    public bool IsAnonymous { get; set; }

    public DateTime FiledDate { get; set; } = DateTime.UtcNow;

    public int? AssignedToId { get; set; }

    public string? InvestigationNotes { get; set; }

    [MaxLength(2000)]
    public string? Resolution { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>Filed, UnderInvestigation, Resolved, or Closed</summary>
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "Filed";

    // Navigation Properties
    public Employee? Employee { get; set; }

    public Employee? AssignedTo { get; set; }
}
