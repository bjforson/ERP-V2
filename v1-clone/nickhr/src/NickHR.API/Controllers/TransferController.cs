using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Services.Transfer;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.HRStaff)]
public class TransferController : ControllerBase
{
    private readonly ITransferService _transferService;
    private readonly ICurrentUserService _currentUser;

    public TransferController(ITransferService transferService, ICurrentUserService currentUser)
    {
        _transferService = transferService;
        _currentUser = currentUser;
    }

    /// <summary>Initiate a transfer, promotion, or demotion.</summary>
    [HttpPost]
    public async Task<IActionResult> Initiate([FromBody] InitiateTransferDto dto)
    {
        try
        {
            if (!Enum.TryParse<TransferType>(dto.Type, true, out var type))
                return BadRequest(ApiResponse<object>.Fail($"Invalid type: {dto.Type}. Valid values: Transfer, Promotion, Demotion."));

            var result = await _transferService.InitiateAsync(
                dto.EmployeeId, type, dto.EffectiveDate,
                dto.ToDepartmentId, dto.ToDesignationId, dto.ToGradeId,
                dto.ToLocationId, dto.NewSalary, dto.Reason);

            return Ok(ApiResponse<object>.Ok(result, "Transfer/Promotion initiated and pending approval."));
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

    /// <summary>Approve a pending transfer/promotion.</summary>
    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> Approve(int id)
    {
        try
        {
            var approverId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _transferService.ApproveAsync(id, approverId);
            return Ok(ApiResponse<object>.Ok(result, "Transfer/Promotion approved and applied."));
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

    /// <summary>Reject a pending transfer/promotion.</summary>
    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> Reject(int id, [FromBody] RejectTransferDto dto)
    {
        try
        {
            var approverId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var result = await _transferService.RejectAsync(id, approverId, dto.Reason);
            return Ok(ApiResponse<object>.Ok(result, "Transfer/Promotion rejected."));
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

    /// <summary>Get all pending transfers/promotions.</summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending()
    {
        var transfers = await _transferService.GetPendingAsync();
        return Ok(ApiResponse<object>.Ok(transfers));
    }

    /// <summary>Get transfer/promotion history for an employee.</summary>
    [HttpGet("employee/{employeeId:int}/history")]
    public async Task<IActionResult> GetEmployeeHistory(int employeeId)
    {
        var history = await _transferService.GetEmployeeHistoryAsync(employeeId);
        return Ok(ApiResponse<object>.Ok(history));
    }
}

public class InitiateTransferDto
{
    public int EmployeeId { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public int ToDepartmentId { get; set; }
    public int ToDesignationId { get; set; }
    public int ToGradeId { get; set; }
    public int? ToLocationId { get; set; }
    public decimal NewSalary { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class RejectTransferDto
{
    public string Reason { get; set; } = string.Empty;
}
