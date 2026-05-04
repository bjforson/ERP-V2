using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Discipline;

public class DisciplinaryCase : BaseEntity
{
    public int EmployeeId { get; set; }

    public DateTime IncidentDate { get; set; }

    [Required]
    public string Description { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Witnesses { get; set; }

    [MaxLength(1000)]
    public string? Evidence { get; set; }

    public DateTime? HearingDate { get; set; }

    public string? HearingNotes { get; set; }

    [MaxLength(500)]
    public string? PanelMembers { get; set; }

    public DisciplinaryAction? Action { get; set; }

    public DateTime? ActionDate { get; set; }

    public bool AppealFiled { get; set; }

    [MaxLength(1000)]
    public string? AppealOutcome { get; set; }

    /// <summary>Open, UnderInvestigation, HearingScheduled, ActionTaken, Closed, or Appealed</summary>
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "Open";

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public ICollection<Warning> Warnings { get; set; } = new List<Warning>();
}
