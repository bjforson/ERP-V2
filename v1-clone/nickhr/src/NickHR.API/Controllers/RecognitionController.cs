using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Services.Recognition;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RecognitionController : ControllerBase
{
    private readonly RecognitionService _service;
    private readonly ICurrentUserService _currentUser;

    public RecognitionController(RecognitionService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    /// <summary>Send kudos/recognition to an employee.</summary>
    [HttpPost]
    public async Task<IActionResult> SendKudos([FromBody] SendKudosRequest request)
    {
        try
        {
            var result = await _service.SendKudosAsync(
                request.SenderEmployeeId,
                request.RecipientEmployeeId,
                request.Message,
                request.Category,
                request.Points);

            return Ok(ApiResponse<object>.Ok(new { result.Id, result.Points }, "Recognition sent successfully."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get public recognition feed.</summary>
    [HttpGet("feed")]
    public async Task<IActionResult> GetFeed([FromQuery] int count = 20)
    {
        var feed = await _service.GetFeedAsync(count);
        return Ok(ApiResponse<object>.Ok(feed));
    }

    /// <summary>Get recognitions received by a specific employee.</summary>
    [HttpGet("employee/{employeeId}")]
    public async Task<IActionResult> GetEmployeeRecognitions(int employeeId)
    {
        var list = await _service.GetEmployeeRecognitionsAsync(employeeId);
        return Ok(ApiResponse<object>.Ok(list));
    }

    /// <summary>Get recognition leaderboard for a month/year.</summary>
    [HttpGet("leaderboard")]
    public async Task<IActionResult> GetLeaderboard([FromQuery] int month, [FromQuery] int year)
    {
        var board = await _service.GetLeaderboardAsync(month, year);
        return Ok(ApiResponse<object>.Ok(board));
    }

    /// <summary>Nominate an employee for Employee of the Month.</summary>
    [HttpPost("employee-of-month/nominate")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> Nominate([FromBody] NominateRequest request)
    {
        try
        {
            var result = await _service.NominateForEmployeeOfMonthAsync(
                request.EmployeeId, request.NominatedById, request.Month, request.Year);
            return Ok(ApiResponse<object>.Ok(new { result.Id }, "Nomination submitted."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get Employee of the Month nominations for a given month/year.</summary>
    [HttpGet("employee-of-month")]
    public async Task<IActionResult> GetEmployeeOfMonth([FromQuery] int month, [FromQuery] int year)
    {
        var list = await _service.GetEmployeeOfMonthAsync(month, year);
        return Ok(ApiResponse<object>.Ok(list));
    }

    /// <summary>Vote for an Employee of the Month nominee.</summary>
    [HttpPost("employee-of-month/{id}/vote")]
    public async Task<IActionResult> Vote(int id)
    {
        try
        {
            var result = await _service.VoteForNomineeAsync(id);
            return Ok(ApiResponse<object>.Ok(new { result.Id, result.Votes }, "Vote recorded."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }
}

public class SendKudosRequest
{
    public int SenderEmployeeId { get; set; }
    public int RecipientEmployeeId { get; set; }
    public string Message { get; set; } = string.Empty;
    public RecognitionCategory Category { get; set; }
    public int Points { get; set; } = 10;
}

public class NominateRequest
{
    public int EmployeeId { get; set; }
    public int NominatedById { get; set; }
    public int Month { get; set; }
    public int Year { get; set; }
}
