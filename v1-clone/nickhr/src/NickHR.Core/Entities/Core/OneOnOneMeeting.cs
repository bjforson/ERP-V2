using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class OneOnOneMeeting : BaseEntity
{
    public int ManagerId { get; set; }

    public int EmployeeId { get; set; }

    public DateTime ScheduledDate { get; set; }

    public DateTime? CompletedDate { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>JSON array of action item strings.</summary>
    public string? ActionItems { get; set; }

    public MeetingStatus Status { get; set; } = MeetingStatus.Scheduled;

    public DateTime? NextMeetingDate { get; set; }

    // Navigation
    public Employee Manager { get; set; } = null!;
    public Employee Employee { get; set; } = null!;
}
