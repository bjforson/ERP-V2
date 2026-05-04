using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.DTOs.Leave;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LeaveController : ControllerBase
{
    private readonly ILeaveService _leaveService;
    private readonly ICurrentUserService _currentUser;

    public LeaveController(ILeaveService leaveService, ICurrentUserService currentUser)
    {
        _leaveService = leaveService;
        _currentUser = currentUser;
    }

    [HttpPost("request")]
    public async Task<ActionResult<ApiResponse<LeaveRequestDto>>> RequestLeave([FromBody] CreateLeaveRequestDto dto)
    {
        try
        {
            var employeeId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _leaveService.RequestLeaveAsync(employeeId, dto);
            return Ok(ApiResponse<LeaveRequestDto>.Ok(result, "Leave request submitted successfully."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<LeaveRequestDto>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<LeaveRequestDto>.Fail(ex.Message));
        }
    }

    [HttpPost("{id:int}/approve")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<ActionResult<ApiResponse<LeaveRequestDto>>> ApproveLeave(int id)
    {
        try
        {
            var approverId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _leaveService.ApproveLeaveAsync(id, approverId);
            return Ok(ApiResponse<LeaveRequestDto>.Ok(result, "Leave request approved."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<LeaveRequestDto>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<LeaveRequestDto>.Fail(ex.Message));
        }
    }

    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<ActionResult<ApiResponse<LeaveRequestDto>>> RejectLeave(
        int id, [FromBody] RejectLeaveRequest body)
    {
        try
        {
            var approverId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _leaveService.RejectLeaveAsync(id, approverId, body.Reason);
            return Ok(ApiResponse<LeaveRequestDto>.Ok(result, "Leave request rejected."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<LeaveRequestDto>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<LeaveRequestDto>.Fail(ex.Message));
        }
    }

    [HttpGet("balances/{employeeId:int}")]
    public async Task<ActionResult<ApiResponse<List<LeaveBalanceDto>>>> GetBalances(int employeeId)
    {
        var balances = await _leaveService.GetBalancesAsync(employeeId);
        return Ok(ApiResponse<List<LeaveBalanceDto>>.Ok(balances));
    }

    [HttpGet("my-balances")]
    public async Task<ActionResult<ApiResponse<List<LeaveBalanceDto>>>> GetMyBalances()
    {
        var employeeId = _currentUser.EmployeeId;
        if (employeeId is null)
            return BadRequest(ApiResponse<List<LeaveBalanceDto>>.Fail("No employee profile linked to current user."));

        var balances = await _leaveService.GetBalancesAsync(employeeId.Value);
        return Ok(ApiResponse<List<LeaveBalanceDto>>.Ok(balances));
    }

    [HttpGet("team-calendar")]
    public async Task<ActionResult<ApiResponse<List<TeamLeaveCalendarDto>>>> GetTeamCalendar(
        [FromQuery] int departmentId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate)
    {
        try
        {
            var calendar = await _leaveService.GetTeamCalendarAsync(departmentId, startDate, endDate);
            return Ok(ApiResponse<List<TeamLeaveCalendarDto>>.Ok(calendar));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<List<TeamLeaveCalendarDto>>.Fail(ex.Message));
        }
    }

    // -------------------------------------------------------------------------
    // Enhanced endpoints
    // -------------------------------------------------------------------------

    /// <summary>GET /api/leave/requests?employeeId=&amp;status=&amp;page=&amp;pageSize=</summary>
    [HttpGet("requests")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<ActionResult<ApiResponse<PagedResult<LeaveRequestDto>>>> GetAllRequests(
        [FromQuery] int? employeeId,
        [FromQuery] LeaveRequestStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        try
        {
            var result = await _leaveService.GetAllRequestsAsync(employeeId, status, page, pageSize);
            return Ok(ApiResponse<PagedResult<LeaveRequestDto>>.Ok(result));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<PagedResult<LeaveRequestDto>>.Fail(ex.Message));
        }
    }

    /// <summary>GET /api/leave/pending-approvals?departmentId=</summary>
    [HttpGet("pending-approvals")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<ActionResult<ApiResponse<List<LeaveRequestDto>>>> GetPendingApprovals(
        [FromQuery] int? departmentId)
    {
        try
        {
            var result = await _leaveService.GetPendingApprovalsAsync(departmentId);
            return Ok(ApiResponse<List<LeaveRequestDto>>.Ok(result));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<List<LeaveRequestDto>>.Fail(ex.Message));
        }
    }

    /// <summary>POST /api/leave/{id}/cancel</summary>
    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<ApiResponse<LeaveRequestDto>>> CancelLeave(int id)
    {
        try
        {
            var employeeId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _leaveService.CancelLeaveAsync(id, employeeId);
            return Ok(ApiResponse<LeaveRequestDto>.Ok(result, "Leave request cancelled."));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<LeaveRequestDto>.Fail(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<LeaveRequestDto>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<LeaveRequestDto>.Fail(ex.Message));
        }
    }

    /// <summary>GET /api/leave/team-calendar-v2?departmentId=&amp;startDate=&amp;endDate=</summary>
    [HttpGet("team-calendar-v2")]
    public async Task<ActionResult<ApiResponse<List<TeamLeaveCalendarDto>>>> GetTeamCalendarV2(
        [FromQuery] int? departmentId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate)
    {
        try
        {
            var calendar = await _leaveService.GetTeamCalendarAsync(departmentId, startDate, endDate);
            return Ok(ApiResponse<List<TeamLeaveCalendarDto>>.Ok(calendar));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<List<TeamLeaveCalendarDto>>.Fail(ex.Message));
        }
    }
}

public class RejectLeaveRequest
{
    public string Reason { get; set; } = string.Empty;
}
