using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Leave;

public class LeaveRequest : BaseEntity
{
    public int EmployeeId { get; set; }

    public int LeavePolicyId { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    [Column(TypeName = "decimal(5,1)")]
    public decimal NumberOfDays { get; set; }

    [MaxLength(1000)]
    public string? Reason { get; set; }

    public LeaveRequestStatus Status { get; set; }

    [MaxLength(500)]
    public string? MedicalCertificatePath { get; set; }

    [MaxLength(1000)]
    public string? HandoverNotes { get; set; }

    public int? ApprovedById { get; set; }

    public DateTime? ApprovedAt { get; set; }

    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public LeavePolicy LeavePolicy { get; set; } = null!;

    public Employee? ApprovedBy { get; set; }
}
