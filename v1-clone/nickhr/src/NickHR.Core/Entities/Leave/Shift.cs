using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Leave;

public class Shift : BaseEntity
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public TimeSpan StartTime { get; set; }

    public TimeSpan EndTime { get; set; }

    public int GracePeriodMinutes { get; set; }

    public bool IsNightShift { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation Properties
    public ICollection<ShiftAssignment> ShiftAssignments { get; set; } = new List<ShiftAssignment>();
}
