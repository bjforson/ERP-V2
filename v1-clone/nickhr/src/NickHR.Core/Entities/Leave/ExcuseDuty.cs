using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;

namespace NickHR.Core.Entities.Leave;

/// <summary>
/// Ghana-specific "Excuse Duty" - partial day absence that does NOT deduct from leave balance.
/// Per Ghana Labour Act, certified medical excuse duty doesn't affect annual leave entitlement.
/// </summary>
public class ExcuseDuty : BaseEntity
{
    public int EmployeeId { get; set; }

    public ExcuseDutyType ExcuseDutyType { get; set; }

    public DateTime Date { get; set; }

    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal DurationHours { get; set; }

    [Required]
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Destination { get; set; }

    public ExcuseDutyStatus Status { get; set; } = ExcuseDutyStatus.Pending;

    public int? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }

    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    // Medical certificate (required for Medical type)
    [MaxLength(500)]
    public string? MedicalCertificatePath { get; set; }

    // Return tracking
    public bool ReturnConfirmed { get; set; }
    public TimeSpan? ReturnTime { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? ActualDurationHours { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation
    public Employee Employee { get; set; } = null!;
    public Employee? ApprovedBy { get; set; }
}
