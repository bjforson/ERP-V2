using NickHR.Core.Enums;

namespace NickHR.Core.DTOs.Leave;

public class LeaveRequestDto
{
    public int Id { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public LeaveType LeaveType { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal NumberOfDays { get; set; }
    public string? Reason { get; set; }
    public LeaveRequestStatus Status { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
}
