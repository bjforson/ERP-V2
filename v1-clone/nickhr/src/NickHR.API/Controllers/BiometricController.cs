using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NickHR.Core.DTOs;
using NickHR.Core.Entities.Leave;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.API.Controllers;

[ApiController]
[Authorize]
[Route("api/attendance/biometric")]
public class BiometricController : ControllerBase
{
    private readonly NickHRDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public BiometricController(NickHRDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    /// <summary>Clock in from a biometric device.</summary>
    [HttpPost("clock-in")]
    public async Task<IActionResult> ClockIn([FromBody] BiometricClockRequest request)
    {
        // FORGERY FIX (Wave 2H): the original lookup used request.EmployeeCode
        // from the body — any authenticated user could clock in/out as anyone.
        // Resolve the employee from the JWT instead, and only honour the body's
        // EmployeeCode if it matches the authenticated user's own code (so legacy
        // clients that still send it keep working without a security regression).
        if (_currentUser.EmployeeId is not int callerId)
            return Unauthorized(ApiResponse<object>.Fail("No employee profile linked to current user."));

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == callerId);
        if (employee == null)
            return NotFound(ApiResponse<object>.Fail("Employee not found."));

        if (!string.IsNullOrEmpty(request.EmployeeCode) &&
            !string.Equals(request.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var today = DateTime.UtcNow.Date;
        var existing = await _db.AttendanceRecords
            .FirstOrDefaultAsync(a => a.EmployeeId == employee.Id && a.Date == today);

        if (existing != null && existing.ClockIn.HasValue)
            return BadRequest(ApiResponse<object>.Fail("Already clocked in today."));

        if (existing == null)
        {
            existing = new AttendanceRecord
            {
                EmployeeId = employee.Id,
                Date = today,
                ClockIn = request.Timestamp ?? DateTime.UtcNow,
                AttendanceType = AttendanceType.Present,
                Notes = $"Biometric device: {request.DeviceId}"
            };
            _db.AttendanceRecords.Add(existing);
        }
        else
        {
            existing.ClockIn = request.Timestamp ?? DateTime.UtcNow;
            existing.Notes = $"Biometric device: {request.DeviceId}";
        }

        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(new { existing.Id, existing.ClockIn }, "Clocked in."));
    }

    /// <summary>Clock out from a biometric device.</summary>
    [HttpPost("clock-out")]
    public async Task<IActionResult> ClockOut([FromBody] BiometricClockRequest request)
    {
        // FORGERY FIX (Wave 2H): same identity-from-claims pattern as ClockIn.
        if (_currentUser.EmployeeId is not int callerId)
            return Unauthorized(ApiResponse<object>.Fail("No employee profile linked to current user."));

        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == callerId);
        if (employee == null)
            return NotFound(ApiResponse<object>.Fail("Employee not found."));

        if (!string.IsNullOrEmpty(request.EmployeeCode) &&
            !string.Equals(request.EmployeeCode, employee.EmployeeCode, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var today = DateTime.UtcNow.Date;
        var existing = await _db.AttendanceRecords
            .FirstOrDefaultAsync(a => a.EmployeeId == employee.Id && a.Date == today);

        if (existing == null || !existing.ClockIn.HasValue)
            return BadRequest(ApiResponse<object>.Fail("No clock-in record found for today."));

        var clockOut = request.Timestamp ?? DateTime.UtcNow;
        existing.ClockOut = clockOut;
        existing.WorkHours = (decimal)(clockOut - existing.ClockIn!.Value).TotalHours;

        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(new { existing.Id, existing.ClockIn, existing.ClockOut, existing.WorkHours }, "Clocked out."));
    }

    /// <summary>Get today's attendance status for an employee.</summary>
    [HttpGet("status/{employeeCode}")]
    public async Task<IActionResult> GetStatus(string employeeCode)
    {
        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.EmployeeCode == employeeCode);

        if (employee == null)
            return NotFound(ApiResponse<object>.Fail("Employee not found."));

        var today = DateTime.UtcNow.Date;
        var record = await _db.AttendanceRecords
            .FirstOrDefaultAsync(a => a.EmployeeId == employee.Id && a.Date == today);

        return Ok(ApiResponse<object>.Ok(new
        {
            EmployeeCode = employeeCode,
            Date = today,
            ClockIn = record?.ClockIn,
            ClockOut = record?.ClockOut,
            WorkHours = record?.WorkHours,
            Status = record == null ? "Not Clocked In" : record.ClockOut.HasValue ? "Clocked Out" : "Clocked In"
        }));
    }
}

public class BiometricClockRequest
{
    public string EmployeeCode { get; set; } = string.Empty;
    public DateTime? Timestamp { get; set; }
    public string? DeviceId { get; set; }
}
