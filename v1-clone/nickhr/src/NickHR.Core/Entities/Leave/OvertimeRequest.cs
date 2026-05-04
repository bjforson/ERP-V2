using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Leave;

public class OvertimeRequest : BaseEntity
{
    public int EmployeeId { get; set; }

    public DateTime Date { get; set; }

    public TimeSpan StartTime { get; set; }

    public TimeSpan EndTime { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal PlannedHours { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? ActualHours { get; set; }

    [Required]
    [MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    public OvertimeRequestStatus Status { get; set; } = OvertimeRequestStatus.Pending;

    public int? ApprovedById { get; set; }

    public DateTime? ApprovedAt { get; set; }

    [MaxLength(1000)]
    public string? RejectionReason { get; set; }

    /// <summary>1.5 for weekday overtime, 2.0 for weekend/holiday overtime.</summary>
    [Column(TypeName = "decimal(4,2)")]
    public decimal Rate { get; set; } = 1.5m;

    [Column(TypeName = "decimal(18,2)")]
    public decimal? PayAmount { get; set; }

    /// <summary>Set when overtime pay is processed via payroll.</summary>
    public int? PayrollRunId { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Core.Employee Employee { get; set; } = null!;

    public Core.Employee? ApprovedBy { get; set; }
}
