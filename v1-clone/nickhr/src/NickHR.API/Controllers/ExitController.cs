using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Services.Exit;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/exit")]
[Authorize(Roles = RoleSets.HRStaff)]
public class ExitController : ControllerBase
{
    private readonly IExitService _exit;

    public ExitController(IExitService exit)
    {
        _exit = exit;
    }

    // ─── Separations ─────────────────────────────────────────────────────────

    /// <summary>Initiate the separation process for an employee.</summary>
    [HttpPost("separations")]
    public async Task<IActionResult> InitiateSeparation([FromBody] InitiateSeparationRequest req)
    {
        try
        {
            var separation = await _exit.InitiateSeparationAsync(
                req.EmployeeId, req.SeparationType, req.Reason,
                req.LastWorkingDate, req.NoticePeriodDays);

            return Ok(ApiResponse<object>.Ok(new
            {
                separation.Id,
                separation.EmployeeId,
                SeparationType = separation.SeparationType.ToString(),
                separation.NoticeDate,
                separation.LastWorkingDate,
                separation.NoticePeriodDays
            }, "Separation initiated and clearance items created."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List separations, optionally filtered by type.</summary>
    [HttpGet("separations")]
    public async Task<IActionResult> GetSeparations([FromQuery] SeparationType? type = null)
    {
        var separations = await _exit.GetSeparationsAsync(type);
        var data = separations.Select(s => new
        {
            s.Id,
            s.EmployeeId,
            Employee = s.Employee is not null ? $"{s.Employee.FirstName} {s.Employee.LastName}" : null,
            SeparationType = s.SeparationType.ToString(),
            s.NoticeDate,
            s.LastWorkingDate,
            s.NoticePeriodDays,
            s.ApprovedById,
            s.ApprovedAt
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Get a separation record by ID including clearance, exit interview, and settlement.</summary>
    [HttpGet("separations/{id}")]
    public async Task<IActionResult> GetSeparationById(int id)
    {
        var separation = await _exit.GetSeparationByIdAsync(id);
        if (separation is null)
            return NotFound(ApiResponse<object>.Fail($"Separation {id} not found."));

        return Ok(ApiResponse<object>.Ok(new
        {
            separation.Id,
            separation.EmployeeId,
            Employee = separation.Employee is not null
                ? $"{separation.Employee.FirstName} {separation.Employee.LastName}" : null,
            SeparationType = separation.SeparationType.ToString(),
            separation.NoticeDate,
            separation.LastWorkingDate,
            separation.NoticePeriodDays,
            separation.Reason,
            separation.ApprovedById,
            separation.ApprovedAt,
            separation.Notes,
            ClearanceItems = separation.ClearanceItems.Select(c => new
            {
                c.Id,
                c.Department,
                c.Description,
                c.IsCleared,
                c.ClearedAt,
                ClearedBy = c.ClearedBy is not null ? $"{c.ClearedBy.FirstName} {c.ClearedBy.LastName}" : null,
                c.Notes
            }),
            FinalSettlement = separation.FinalSettlement is null ? null : new
            {
                separation.FinalSettlement.Id,
                separation.FinalSettlement.LeaveEncashment,
                separation.FinalSettlement.ProRatedBonus,
                separation.FinalSettlement.GratuityAmount,
                separation.FinalSettlement.LoanRecovery,
                separation.FinalSettlement.OtherDeductions,
                separation.FinalSettlement.TotalSettlement,
                separation.FinalSettlement.ProcessedAt,
                separation.FinalSettlement.PaymentReference
            }
        }));
    }

    /// <summary>Approve a separation request.</summary>
    [HttpPut("separations/{id}/approve")]
    public async Task<IActionResult> ApproveSeparation(int id, [FromBody] ApproveSeparationRequest req)
    {
        try
        {
            var separation = await _exit.ApproveSeparationAsync(id, req.ApprovedById);
            return Ok(ApiResponse<object>.Ok(new
            {
                separation.Id,
                separation.ApprovedById,
                separation.ApprovedAt
            }, "Separation approved."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    // ─── Clearance ───────────────────────────────────────────────────────────

    /// <summary>Mark a clearance item as cleared or pending.</summary>
    [HttpPut("clearance/{clearanceItemId}")]
    public async Task<IActionResult> UpdateClearanceItem(int clearanceItemId, [FromBody] UpdateClearanceRequest req)
    {
        try
        {
            var item = await _exit.UpdateClearanceItemAsync(
                clearanceItemId, req.IsCleared, req.ClearedById, req.Notes);

            return Ok(ApiResponse<object>.Ok(new
            {
                item.Id,
                item.Department,
                item.IsCleared,
                item.ClearedAt,
                item.Notes
            }, "Clearance item updated."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    // ─── Exit Interview ──────────────────────────────────────────────────────

    /// <summary>Record an exit interview for a separation.</summary>
    [HttpPost("separations/{separationId}/exit-interview")]
    public async Task<IActionResult> RecordExitInterview(int separationId, [FromBody] ExitInterviewRequest req)
    {
        try
        {
            var interview = await _exit.RecordExitInterviewAsync(
                separationId, req.InterviewerId, req.ReasonForLeaving,
                req.WouldRecommend, req.Feedback, req.OverallExperience, req.Suggestions);

            return Ok(ApiResponse<object>.Ok(new
            {
                interview.Id,
                interview.SeparationId,
                interview.InterviewDate,
                interview.InterviewerId,
                interview.ReasonForLeaving,
                interview.WouldRecommend,
                interview.OverallExperience
            }, "Exit interview recorded."));
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

    // ─── Final Settlement ────────────────────────────────────────────────────

    /// <summary>Process the final settlement for a separation.</summary>
    [HttpPost("separations/{separationId}/settlement")]
    public async Task<IActionResult> ProcessFinalSettlement(int separationId, [FromBody] FinalSettlementRequest req)
    {
        try
        {
            var settlement = await _exit.ProcessFinalSettlementAsync(
                separationId, req.LeaveEncashment, req.ProRatedBonus,
                req.Gratuity, req.LoanRecovery, req.OtherDeductions, req.PaymentReference);

            return Ok(ApiResponse<object>.Ok(new
            {
                settlement.Id,
                settlement.SeparationId,
                settlement.LeaveEncashment,
                settlement.ProRatedBonus,
                settlement.GratuityAmount,
                settlement.LoanRecovery,
                settlement.OtherDeductions,
                settlement.TotalSettlement,
                settlement.ProcessedAt,
                settlement.PaymentReference
            }, "Final settlement processed and employee status updated."));
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
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

public record InitiateSeparationRequest(
    int EmployeeId,
    SeparationType SeparationType,
    string? Reason,
    DateTime LastWorkingDate,
    int NoticePeriodDays);

public record ApproveSeparationRequest(int ApprovedById);

public record UpdateClearanceRequest(bool IsCleared, int ClearedById, string? Notes);

public record ExitInterviewRequest(
    int InterviewerId,
    string? ReasonForLeaving,
    bool? WouldRecommend,
    string? Feedback,
    int? OverallExperience,
    string? Suggestions);

public record FinalSettlementRequest(
    decimal LeaveEncashment,
    decimal ProRatedBonus,
    decimal Gratuity,
    decimal LoanRecovery,
    decimal OtherDeductions,
    string? PaymentReference);
