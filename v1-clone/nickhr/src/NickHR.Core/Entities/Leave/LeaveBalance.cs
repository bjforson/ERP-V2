using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Leave;

public class LeaveBalance : BaseEntity
{
    public int EmployeeId { get; set; }

    public int LeavePolicyId { get; set; }

    public int Year { get; set; }

    [Column(TypeName = "decimal(5,1)")]
    public decimal Entitled { get; set; }

    [Column(TypeName = "decimal(5,1)")]
    public decimal Taken { get; set; }

    [Column(TypeName = "decimal(5,1)")]
    public decimal Pending { get; set; }

    [Column(TypeName = "decimal(5,1)")]
    public decimal CarriedForward { get; set; }

    [NotMapped]
    public decimal Available => Entitled + CarriedForward - Taken - Pending;

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public LeavePolicy LeavePolicy { get; set; } = null!;
}
