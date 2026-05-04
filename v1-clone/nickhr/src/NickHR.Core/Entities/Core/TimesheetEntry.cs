using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Core;

public class TimesheetEntry : BaseEntity
{
    public int EmployeeId { get; set; }

    public int ProjectId { get; set; }

    public DateTime Date { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal Hours { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsBillable { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "Draft"; // Draft, Submitted, Approved

    // Navigation
    public Employee Employee { get; set; } = null!;
    public Project Project { get; set; } = null!;
}
