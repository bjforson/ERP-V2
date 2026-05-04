using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Leave;

public class LeavePolicy : BaseEntity
{
    public LeaveType LeaveType { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column(TypeName = "decimal(5,1)")]
    public decimal DefaultDays { get; set; }

    [Column(TypeName = "decimal(5,1)")]
    public decimal MaxAccumulation { get; set; }

    [Column(TypeName = "decimal(5,1)")]
    public decimal CarryForwardMax { get; set; }

    public bool IsCarryForwardAllowed { get; set; }

    public bool RequiresMedicalCertificate { get; set; }

    public int MinServiceMonthsRequired { get; set; }

    public int? GradeId { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? Description { get; set; }

    // Navigation Properties
    public Grade? Grade { get; set; }

    public ICollection<LeaveBalance> LeaveBalances { get; set; } = new List<LeaveBalance>();
}
