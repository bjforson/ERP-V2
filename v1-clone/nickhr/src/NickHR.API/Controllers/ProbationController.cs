using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Services.Probation;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.HRStaff)]
public class ProbationController : ControllerBase
{
    private readonly IProbationService _probationService;
    private readonly ICurrentUserService _currentUser;

    public ProbationController(IProbationService probationService, ICurrentUserService currentUser)
    {
        _probationService = probationService;
        _currentUser = currentUser;
    }

    /// <summary>Get employees approaching probation end date.</summary>
    [HttpGet("approaching")]
    public async Task<IActionResult> GetApproaching([FromQuery] int daysThreshold = 30)
    {
        var employees = await _probationService.GetEmployeesApproachingProbationEndAsync(daysThreshold);
        return Ok(ApiResponse<object>.Ok(employees.Select(e => new
        {
            e.Id,
            FullName = $"{e.FirstName} {e.LastName}",
            e.EmployeeCode,
            Department = e.Department?.Name,
            Designation = e.Designation?.Title,
            e.ProbationEndDate,
            DaysRemaining = (e.ProbationEndDate!.Value - DateTime.UtcNow).Days
        })));
    }

    /// <summary>Create a probation review for an employee.</summary>
    [HttpPost("review")]
    public async Task<IActionResult> CreateReview([FromBody] CreateProbationReviewDto dto)
    {
        try
        {
            var reviewedById = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var review = await _probationService.CreateReviewAsync(dto.EmployeeId, dto.ProbationEndDate, reviewedById);
            return Ok(ApiResponse<object>.Ok(review, "Probation review created."));
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

    /// <summary>Complete a probation review with a decision.</summary>
    [HttpPost("review/{id:int}/complete")]
    public async Task<IActionResult> CompleteReview(int id, [FromBody] CompleteProbationReviewDto dto)
    {
        try
        {
            if (!Enum.TryParse<ProbationDecision>(dto.Decision, true, out var decision))
                return BadRequest(ApiResponse<object>.Fail($"Invalid decision: {dto.Decision}. Valid values: Confirm, Extend, Terminate."));

            var review = await _probationService.CompleteReviewAsync(
                id, decision, dto.ExtensionMonths, dto.ManagerComments ?? "", dto.HRComments ?? "");

            return Ok(ApiResponse<object>.Ok(review, "Probation review completed."));
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

    /// <summary>Get all probation reviews, optionally filtered by status.</summary>
    [HttpGet("reviews")]
    public async Task<IActionResult> GetReviews([FromQuery] string? status = null)
    {
        var reviews = await _probationService.GetReviewsAsync(status);
        return Ok(ApiResponse<object>.Ok(reviews));
    }
}

public class CreateProbationReviewDto
{
    public int EmployeeId { get; set; }
    public DateTime ProbationEndDate { get; set; }
}

public class CompleteProbationReviewDto
{
    public string Decision { get; set; } = string.Empty;
    public int? ExtensionMonths { get; set; }
    public string? ManagerComments { get; set; }
    public string? HRComments { get; set; }
}
