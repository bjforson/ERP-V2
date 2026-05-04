using NickHR.Core.Enums;

namespace NickHR.Core.DTOs.Leave;

public class TeamLeaveCalendarDto
{
    public string EmployeeName { get; set; } = string.Empty;
    public LeaveType LeaveType { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public LeaveRequestStatus Status { get; set; }
}
