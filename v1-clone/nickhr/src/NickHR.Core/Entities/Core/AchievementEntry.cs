using NickHR.Core.Enums;
using NickHR.Core.Entities.Performance;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class AchievementEntry : BaseEntity
{
    public int EmployeeId { get; set; }

    public DateTime EntryDate { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public AchievementCategory Category { get; set; } = AchievementCategory.Achievement;

    public int? LinkedGoalId { get; set; }

    // Navigation
    public Employee Employee { get; set; } = null!;
    public Goal? LinkedGoal { get; set; }
}
