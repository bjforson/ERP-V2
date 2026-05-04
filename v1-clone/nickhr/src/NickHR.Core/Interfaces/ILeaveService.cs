using NickHR.Core.DTOs;
using NickHR.Core.DTOs.Leave;
using NickHR.Core.Enums;

namespace NickHR.Core.Interfaces;

public interface ILeaveService
{
    Task<LeaveRequestDto> RequestLeaveAsync(int employeeId, CreateLeaveRequestDto dto);
    Task<LeaveRequestDto> ApproveLeaveAsync(int requestId, int approverId);
    Task<LeaveRequestDto> RejectLeaveAsync(int requestId, int approverId, string reason);
    Task<List<LeaveBalanceDto>> GetBalancesAsync(int employeeId);
    Task<List<TeamLeaveCalendarDto>> GetTeamCalendarAsync(int departmentId, DateTime start, DateTime end);

    // Enhanced methods
    Task<PagedResult<LeaveRequestDto>> GetAllRequestsAsync(int? employeeId, LeaveRequestStatus? status, int page, int pageSize);
    Task<List<LeaveRequestDto>> GetPendingApprovalsAsync(int? departmentId);
    Task<LeaveRequestDto> CancelLeaveAsync(int requestId, int employeeId);
    Task<List<TeamLeaveCalendarDto>> GetTeamCalendarAsync(int? departmentId, DateTime startDate, DateTime endDate);
}
