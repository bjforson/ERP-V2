using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Services.Performance;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/performance")]
[Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
public class PerformanceController : ControllerBase
{
    private readonly IPerformanceService _performance;

    public PerformanceController(IPerformanceService performance)
    {
        _performance = performance;
    }

    // ─── Appraisal Cycles ────────────────────────────────────────────────────

    /// <summary>Create a new appraisal cycle.</summary>
    [HttpPost("cycles")]
    public async Task<IActionResult> CreateCycle([FromBody] CreateCycleRequest req)
    {
        try
        {
            var cycle = await _performance.CreateCycleAsync(req.Name, req.StartDate, req.EndDate, req.Description);
            return Ok(ApiResponse<object>.Ok(new
            {
                cycle.Id,
                cycle.Name,
                cycle.StartDate,
                cycle.EndDate,
                cycle.IsActive
            }, "Appraisal cycle created."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List all appraisal cycles.</summary>
    [HttpGet("cycles")]
    public async Task<IActionResult> GetCycles()
    {
        var cycles = await _performance.GetCyclesAsync();
        var data = cycles.Select(c => new
        {
            c.Id,
            c.Name,
            c.StartDate,
            c.EndDate,
            c.Description,
            c.IsActive
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Get the currently active appraisal cycle.</summary>
    [HttpGet("cycles/active")]
    public async Task<IActionResult> GetActiveCycle()
    {
        var cycle = await _performance.GetActiveCycleAsync();
        if (cycle is null)
            return Ok(ApiResponse<object>.Ok((object)null!, "No active cycle found."));

        return Ok(ApiResponse<object>.Ok(new
        {
            cycle.Id,
            cycle.Name,
            cycle.StartDate,
            cycle.EndDate,
            cycle.Description
        }));
    }

    // ─── Goals ───────────────────────────────────────────────────────────────

    /// <summary>Create a goal for an employee.</summary>
    [HttpPost("goals")]
    public async Task<IActionResult> CreateGoal([FromBody] CreateGoalRequest req)
    {
        try
        {
            var goal = await _performance.CreateGoalAsync(
                req.EmployeeId, req.AppraisalCycleId, req.Title,
                req.Description, req.TargetValue, req.Weight, req.DueDate);

            return Ok(ApiResponse<object>.Ok(new
            {
                goal.Id,
                goal.Title,
                goal.Weight,
                goal.Status
            }, "Goal created."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get goals for an employee, optionally filtered by cycle.</summary>
    [HttpGet("goals/employee/{employeeId}")]
    public async Task<IActionResult> GetEmployeeGoals(int employeeId, [FromQuery] int? cycleId = null)
    {
        var goals = await _performance.GetEmployeeGoalsAsync(employeeId, cycleId);
        var data = goals.Select(g => new
        {
            g.Id,
            g.Title,
            g.Description,
            g.TargetValue,
            g.AchievedValue,
            g.Weight,
            g.ProgressPercent,
            g.DueDate,
            g.Status,
            Cycle = g.AppraisalCycle?.Name
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Update progress on a goal.</summary>
    [HttpPut("goals/{goalId}/progress")]
    public async Task<IActionResult> UpdateGoalProgress(int goalId, [FromBody] UpdateGoalProgressRequest req)
    {
        try
        {
            var goal = await _performance.UpdateGoalProgressAsync(
                goalId, req.ProgressPercent, req.AchievedValue, req.Status);

            return Ok(ApiResponse<object>.Ok(new
            {
                goal.Id,
                goal.ProgressPercent,
                goal.AchievedValue,
                goal.Status
            }, "Goal progress updated."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Delete a goal (soft delete).</summary>
    [HttpDelete("goals/{goalId}")]
    public async Task<IActionResult> DeleteGoal(int goalId)
    {
        try
        {
            await _performance.DeleteGoalAsync(goalId);
            return Ok(ApiResponse<object>.Ok((object)null!, "Goal deleted."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    // ─── Appraisals ──────────────────────────────────────────────────────────

    /// <summary>Create an appraisal form.</summary>
    [HttpPost("appraisals")]
    public async Task<IActionResult> CreateAppraisal([FromBody] CreateAppraisalRequest req)
    {
        try
        {
            var form = await _performance.CreateAppraisalAsync(
                req.AppraisalCycleId, req.EmployeeId, req.ReviewerId);

            return Ok(ApiResponse<object>.Ok(new
            {
                form.Id,
                form.AppraisalCycleId,
                form.EmployeeId,
                form.ReviewerId,
                Status = form.Status.ToString()
            }, "Appraisal created."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List appraisals, optionally filtered by cycle and/or employee.</summary>
    [HttpGet("appraisals")]
    public async Task<IActionResult> GetAppraisals([FromQuery] int? cycleId = null, [FromQuery] int? employeeId = null)
    {
        var forms = await _performance.GetAppraisalsAsync(cycleId, employeeId);
        var data = forms.Select(f => new
        {
            f.Id,
            Employee = f.Employee is not null ? $"{f.Employee.FirstName} {f.Employee.LastName}" : null,
            f.EmployeeId,
            Reviewer = f.Reviewer is not null ? $"{f.Reviewer.FirstName} {f.Reviewer.LastName}" : null,
            f.ReviewerId,
            Cycle = f.AppraisalCycle?.Name,
            f.SelfRating,
            f.ManagerRating,
            f.FinalRating,
            Status = f.Status.ToString(),
            f.SubmittedAt,
            f.ReviewedAt
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Get a single appraisal by ID.</summary>
    [HttpGet("appraisals/{id}")]
    public async Task<IActionResult> GetAppraisalById(int id)
    {
        var form = await _performance.GetAppraisalByIdAsync(id);
        if (form is null)
            return NotFound(ApiResponse<object>.Fail($"Appraisal {id} not found."));

        return Ok(ApiResponse<object>.Ok(new
        {
            form.Id,
            Employee = form.Employee is not null ? $"{form.Employee.FirstName} {form.Employee.LastName}" : null,
            form.EmployeeId,
            Reviewer = form.Reviewer is not null ? $"{form.Reviewer.FirstName} {form.Reviewer.LastName}" : null,
            form.ReviewerId,
            Cycle = form.AppraisalCycle?.Name,
            form.SelfRating,
            form.SelfComments,
            form.ManagerRating,
            form.ManagerComments,
            form.FinalRating,
            Status = form.Status.ToString(),
            form.SubmittedAt,
            form.ReviewedAt
        }));
    }

    /// <summary>Submit self-rating for an appraisal. Any authenticated employee may submit their own.</summary>
    [HttpPut("appraisals/{appraisalId}/self-rating")]
    [Authorize]
    public async Task<IActionResult> SubmitSelfRating(int appraisalId, [FromBody] SelfRatingRequest req)
    {
        try
        {
            var form = await _performance.SubmitSelfRatingAsync(appraisalId, req.Rating, req.Comments);
            return Ok(ApiResponse<object>.Ok(new
            {
                form.Id,
                form.SelfRating,
                form.SelfComments,
                Status = form.Status.ToString()
            }, "Self rating submitted."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Submit manager rating to complete an appraisal.</summary>
    [HttpPut("appraisals/{appraisalId}/manager-rating")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> SubmitManagerRating(int appraisalId, [FromBody] ManagerRatingRequest req)
    {
        try
        {
            var form = await _performance.SubmitManagerRatingAsync(
                appraisalId, req.ManagerRating, req.FinalRating, req.Comments);

            return Ok(ApiResponse<object>.Ok(new
            {
                form.Id,
                form.ManagerRating,
                form.FinalRating,
                form.ManagerComments,
                Status = form.Status.ToString(),
                form.ReviewedAt
            }, "Manager rating submitted."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    // ─── Low Performers ──────────────────────────────────────────────────────

    /// <summary>Get employees with final rating below the given threshold in a cycle.</summary>
    [HttpGet("cycles/{cycleId}/low-performers")]
    public async Task<IActionResult> GetLowPerformers(int cycleId, [FromQuery] decimal threshold = 3.0m)
    {
        var forms = await _performance.GetLowPerformersAsync(cycleId, threshold);
        var data = forms.Select(f => new
        {
            f.EmployeeId,
            Employee = f.Employee is not null ? $"{f.Employee.FirstName} {f.Employee.LastName}" : null,
            f.FinalRating,
            f.ManagerComments
        });
        return Ok(ApiResponse<object>.Ok(data));
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

public record CreateCycleRequest(string Name, DateTime StartDate, DateTime EndDate, string? Description);

public record CreateGoalRequest(int EmployeeId, int AppraisalCycleId, string Title, string? Description,
    string? TargetValue, decimal Weight, DateTime? DueDate);

public record UpdateGoalProgressRequest(decimal ProgressPercent, string? AchievedValue, string Status);

public record CreateAppraisalRequest(int AppraisalCycleId, int EmployeeId, int ReviewerId);

public record SelfRatingRequest(decimal Rating, string? Comments);

public record ManagerRatingRequest(decimal ManagerRating, decimal FinalRating, string? Comments);
