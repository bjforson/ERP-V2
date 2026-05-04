using NickHR.Core.Enums;

namespace NickHR.Core.DTOs.Leave;

public class LeaveBalanceDto
{
    public LeaveType LeaveType { get; set; }
    public decimal Entitled { get; set; }
    public decimal Taken { get; set; }
    public decimal Pending { get; set; }
    public decimal CarriedForward { get; set; }
    public decimal Available { get; set; }
}
