using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Core;

public class TransferPromotion : BaseEntity
{
    public int EmployeeId { get; set; }

    public TransferType Type { get; set; }

    public DateTime EffectiveDate { get; set; }

    [MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    public int FromDepartmentId { get; set; }

    public int ToDepartmentId { get; set; }

    public int FromDesignationId { get; set; }

    public int ToDesignationId { get; set; }

    public int FromGradeId { get; set; }

    public int ToGradeId { get; set; }

    public int? FromLocationId { get; set; }

    public int? ToLocationId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OldBasicSalary { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NewBasicSalary { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    public int? ApprovedById { get; set; }

    public DateTime? ApprovedAt { get; set; }

    [MaxLength(1000)]
    public string? RejectionReason { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public Department FromDepartment { get; set; } = null!;

    public Department ToDepartment { get; set; } = null!;

    public Designation FromDesignation { get; set; } = null!;

    public Designation ToDesignation { get; set; } = null!;

    public Grade FromGrade { get; set; } = null!;

    public Grade ToGrade { get; set; } = null!;

    public Location? FromLocation { get; set; }

    public Location? ToLocation { get; set; }

    public Employee? ApprovedBy { get; set; }
}
