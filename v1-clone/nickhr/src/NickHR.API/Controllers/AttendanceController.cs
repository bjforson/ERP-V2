using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Interfaces;
using NickHR.Services.Attendance;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AttendanceController : ControllerBase
{
    private readonly IAttendanceService _attendanceService;
    private readonly ICurrentUserService _currentUser;

    public AttendanceController(IAttendanceService attendanceService, ICurrentUserService currentUser)
    {
        _attendanceService = attendanceService;
        _currentUser = currentUser;
    }

    /// <summary>POST /api/attendance/clock-in</summary>
    [HttpPost("clock-in")]
    public async Task<ActionResult<ApiResponse<AttendanceRecordDto>>> ClockIn([FromBody] ClockInRequest? body)
    {
        try
        {
            var employeeId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _attendanceService.ClockInAsync(
                employeeId,
                body?.IpAddress ?? HttpContext.Connection.RemoteIpAddress?.ToString(),
                body?.Latitude,
                body?.Longitude);

            return Ok(ApiResponse<AttendanceRecordDto>.Ok(result, "Clocked in successfully."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AttendanceRecordDto>.Fail(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<AttendanceRecordDto>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<AttendanceRecordDto>.Fail(ex.Message));
        }
    }

    /// <summary>POST /api/attendance/clock-out</summary>
    [HttpPost("clock-out")]
    public async Task<ActionResult<ApiResponse<AttendanceRecordDto>>> ClockOut()
    {
        try
        {
            var employeeId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _attendanceService.ClockOutAsync(employeeId);
            return Ok(ApiResponse<AttendanceRecordDto>.Ok(result, "Clocked out successfully."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AttendanceRecordDto>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<AttendanceRecordDto>.Fail(ex.Message));
        }
    }

    /// <summary>GET /api/attendance/today</summary>
    [HttpGet("today")]
    public async Task<ActionResult<ApiResponse<AttendanceRecordDto?>>> GetTodayStatus()
    {
        try
        {
            var employeeId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _attendanceService.GetTodayStatusAsync(employeeId);
            return Ok(ApiResponse<AttendanceRecordDto?>.Ok(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<AttendanceRecordDto?>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<AttendanceRecordDto?>.Fail(ex.Message));
        }
    }

    /// <summary>GET /api/attendance/my-records?month=&amp;year=</summary>
    [HttpGet("my-records")]
    public async Task<ActionResult<ApiResponse<List<AttendanceRecordDto>>>> GetMyRecords(
        [FromQuery] int? month,
        [FromQuery] int? year)
    {
        try
        {
            var employeeId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var now = DateTime.UtcNow;
            var targetMonth = month ?? now.Month;
            var targetYear = year ?? now.Year;

            var result = await _attendanceService.GetMyAttendanceAsync(employeeId, targetMonth, targetYear);
            return Ok(ApiResponse<List<AttendanceRecordDto>>.Ok(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<List<AttendanceRecordDto>>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<List<AttendanceRecordDto>>.Fail(ex.Message));
        }
    }

    /// <summary>GET /api/attendance/report?departmentId=&amp;startDate=&amp;endDate=</summary>
    [HttpGet("report")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<ActionResult<ApiResponse<List<AttendanceReportEntryDto>>>> GetReport(
        [FromQuery] int? departmentId,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate)
    {
        try
        {
            var now = DateTime.UtcNow;
            var start = startDate ?? new DateTime(now.Year, now.Month, 1);
            var end = endDate ?? now.Date;

            var result = await _attendanceService.GetAttendanceReportAsync(departmentId, start, end);
            return Ok(ApiResponse<List<AttendanceReportEntryDto>>.Ok(result));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<List<AttendanceReportEntryDto>>.Fail(ex.Message));
        }
    }
}
