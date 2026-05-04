using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Interfaces;
using NickHR.Services.Overtime;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class OvertimeController : ControllerBase
{
    private readonly IOvertimeService _overtimeService;
    private readonly ICurrentUserService _currentUser;

    public OvertimeController(IOvertimeService overtimeService, ICurrentUserService currentUser)
    {
        _overtimeService = overtimeService;
        _currentUser = currentUser;
    }

    /// <summary>Submit an overtime request.</summary>
    [HttpPost("request")]
    public async Task<IActionResult> SubmitRequest([FromBody] OvertimeRequestDto dto)
    {
        try
        {
            var employeeId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _overtimeService.RequestOvertimeAsync(
                employeeId, dto.Date, dto.StartTime, dto.EndTime, dto.Reason);
            return Ok(ApiResponse<object>.Ok(MapToResponse(result), "Overtime request submitted."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get my overtime requests.</summary>
    [HttpGet("my")]
    public async Task<IActionResult> GetMy([FromQuery] int? month, [FromQuery] int? year)
    {
        var employeeId = _currentUser.EmployeeId;
        if (employeeId == null) return Unauthorized();

        var requests = await _overtimeService.GetMyRequestsAsync(employeeId.Value, month, year);
        return Ok(ApiResponse<object>.Ok(requests.Select(MapToResponse)));
    }

    /// <summary>Get pending overtime requests for approval.</summary>
    [HttpGet("pending")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> GetPending()
    {
        var requests = await _overtimeService.GetPendingApprovalsAsync();
        return Ok(ApiResponse<object>.Ok(requests.Select(MapToResponse)));
    }

    /// <summary>Approve an overtime request.</summary>
    [HttpPost("{id:int}/approve")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> Approve(int id)
    {
        try
        {
            var approverId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _overtimeService.ApproveAsync(id, approverId);
            return Ok(ApiResponse<object>.Ok(MapToResponse(result), "Overtime approved."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Reject an overtime request.</summary>
    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> Reject(int id, [FromBody] RejectOvertimeDto dto)
    {
        try
        {
            var approverId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _overtimeService.RejectAsync(id, approverId, dto.Reason);
            return Ok(ApiResponse<object>.Ok(MapToResponse(result), "Overtime rejected."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Mark overtime as completed with actual hours.</summary>
    [HttpPost("{id:int}/complete")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> Complete(int id, [FromBody] CompleteOvertimeDto dto)
    {
        try
        {
            var result = await _overtimeService.CompleteAsync(id, dto.ActualHours);
            return Ok(ApiResponse<object>.Ok(MapToResponse(result), "Overtime completed."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get overtime for payroll processing.</summary>
    [HttpGet("payroll/{employeeId:int}")]
    [Authorize(Roles = RoleSets.SeniorHROrPayroll)]
    public async Task<IActionResult> GetForPayroll(int employeeId, [FromQuery] int month, [FromQuery] int year)
    {
        var requests = await _overtimeService.GetOvertimeForPayrollAsync(employeeId, month, year);
        return Ok(ApiResponse<object>.Ok(requests.Select(MapToResponse)));
    }

    private static object MapToResponse(NickHR.Core.Entities.Leave.OvertimeRequest o) => new
    {
        o.Id,
        o.EmployeeId,
        o.Date,
        o.StartTime,
        o.EndTime,
        o.PlannedHours,
        o.ActualHours,
        o.Reason,
        Status = o.Status.ToString(),
        o.ApprovedById,
        o.ApprovedAt,
        o.RejectionReason,
        o.Rate,
        o.PayAmount,
        o.PayrollRunId,
        o.Notes
    };
}

public class OvertimeRequestDto
{
    public DateTime Date { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class RejectOvertimeDto
{
    public string Reason { get; set; } = string.Empty;
}

public class CompleteOvertimeDto
{
    public decimal ActualHours { get; set; }
}
