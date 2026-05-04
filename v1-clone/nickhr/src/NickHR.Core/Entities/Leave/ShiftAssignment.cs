using NickHR.Core.Entities.Core;

namespace NickHR.Core.Entities.Leave;

public class ShiftAssignment : BaseEntity
{
    public int EmployeeId { get; set; }

    public int ShiftId { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public Shift Shift { get; set; } = null!;
}
