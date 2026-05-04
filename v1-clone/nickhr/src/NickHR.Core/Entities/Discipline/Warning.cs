using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Discipline;

public class Warning : BaseEntity
{
    public int DisciplinaryCaseId { get; set; }

    public int EmployeeId { get; set; }

    public DisciplinaryAction WarningType { get; set; }

    public DateTime IssueDate { get; set; }

    public DateTime? ExpiryDate { get; set; }

    [Required]
    public string Description { get; set; } = string.Empty;

    public int IssuedById { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    // Navigation Properties
    public DisciplinaryCase DisciplinaryCase { get; set; } = null!;

    public Employee Employee { get; set; } = null!;

    public Employee IssuedBy { get; set; } = null!;
}
