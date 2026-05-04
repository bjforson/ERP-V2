using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Interfaces;
using NickHR.Services.Meeting;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MeetingController : ControllerBase
{
    private readonly MeetingService _service;
    private readonly ICurrentUserService _currentUser;

    public MeetingController(MeetingService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    /// <summary>Schedule a 1-on-1 meeting.</summary>
    [HttpPost]
    public async Task<IActionResult> Schedule([FromBody] ScheduleMeetingRequest request)
    {
        try
        {
            var meeting = await _service.ScheduleAsync(
                request.ManagerId, request.EmployeeId, request.ScheduledDate);
            return Ok(ApiResponse<object>.Ok(new { meeting.Id, meeting.ScheduledDate }, "Meeting scheduled."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Mark a meeting as completed with notes and action items.</summary>
    [HttpPost("{meetingId}/complete")]
    public async Task<IActionResult> Complete(int meetingId, [FromBody] CompleteMeetingRequest request)
    {
        try
        {
            var meeting = await _service.CompleteAsync(
                meetingId, request.Notes, request.ActionItems, request.NextMeetingDate);
            return Ok(ApiResponse<object>.Ok(new { meeting.Id, meeting.Status }, "Meeting marked as completed."));
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

    /// <summary>Cancel a scheduled meeting.</summary>
    [HttpPost("{meetingId}/cancel")]
    public async Task<IActionResult> Cancel(int meetingId)
    {
        try
        {
            var meeting = await _service.CancelAsync(meetingId);
            return Ok(ApiResponse<object>.Ok(new { meeting.Id, meeting.Status }, "Meeting cancelled."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get upcoming meetings, optionally filtered by manager.</summary>
    [HttpGet("upcoming")]
    public async Task<IActionResult> GetUpcoming([FromQuery] int? managerId = null)
    {
        var meetings = await _service.GetUpcomingAsync(managerId);
        return Ok(ApiResponse<object>.Ok(meetings));
    }

    /// <summary>Get completed meeting history for an employee.</summary>
    [HttpGet("history/{employeeId}")]
    public async Task<IActionResult> GetHistory(int employeeId)
    {
        var meetings = await _service.GetHistoryAsync(employeeId);
        return Ok(ApiResponse<object>.Ok(meetings));
    }

    /// <summary>Get all meetings for an employee (as manager or as direct report).</summary>
    [HttpGet("my/{employeeId}")]
    public async Task<IActionResult> GetMyMeetings(int employeeId)
    {
        var meetings = await _service.GetMyMeetingsAsync(employeeId);
        return Ok(ApiResponse<object>.Ok(meetings));
    }
}

public class ScheduleMeetingRequest
{
    public int ManagerId { get; set; }
    public int EmployeeId { get; set; }
    public DateTime ScheduledDate { get; set; }
}

public class CompleteMeetingRequest
{
    public string? Notes { get; set; }
    public string? ActionItems { get; set; }
    public DateTime? NextMeetingDate { get; set; }
}
