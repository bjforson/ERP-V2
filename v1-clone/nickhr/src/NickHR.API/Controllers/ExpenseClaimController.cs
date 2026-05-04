using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/expense-claims")]
[Authorize]
public class ExpenseClaimController : ControllerBase
{
    private readonly IExpenseClaimService _service;
    private readonly ICurrentUserService _currentUser;

    public ExpenseClaimController(IExpenseClaimService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    // POST /api/expense-claims/submit
    [HttpPost("submit")]
    public async Task<ActionResult<ApiResponse<ExpenseClaimDto>>> Submit([FromBody] SubmitExpenseClaimRequest req)
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var result = await _service.SubmitAsync(employeeId, req.Category, req.Description, req.Amount, req.ReceiptPath, req.Notes);
            return Ok(ApiResponse<ExpenseClaimDto>.Ok(result, "Expense claim submitted."));
        }
        catch (ArgumentException ex) { return BadRequest(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
    }

    // GET /api/expense-claims/my
    [HttpGet("my")]
    public async Task<ActionResult<ApiResponse<List<ExpenseClaimDto>>>> GetMy()
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var result = await _service.GetMyClaimsAsync(employeeId);
            return Ok(ApiResponse<List<ExpenseClaimDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<ExpenseClaimDto>>.Fail(ex.Message)); }
    }

    // GET /api/expense-claims/review?status=
    [HttpGet("review")]
    [Authorize(Roles = RoleSets.HRStaffOrFinance)]
    public async Task<ActionResult<ApiResponse<List<ExpenseClaimDto>>>> GetForReview([FromQuery] ExpenseClaimStatus? status)
    {
        try
        {
            var result = await _service.GetForReviewAsync(status);
            return Ok(ApiResponse<List<ExpenseClaimDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<ExpenseClaimDto>>.Fail(ex.Message)); }
    }

    // POST /api/expense-claims/{id}/approve
    [HttpPost("{id:int}/approve")]
    [Authorize(Roles = RoleSets.HRStaffOrFinance)]
    public async Task<ActionResult<ApiResponse<ExpenseClaimDto>>> Approve(int id, [FromBody] ApproveExpenseRequest req)
    {
        try
        {
            var approverId = RequireEmployeeId();
            var result = await _service.ApproveAsync(id, approverId, req.ApprovedAmount);
            return Ok(ApiResponse<ExpenseClaimDto>.Ok(result, "Claim approved."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
    }

    // POST /api/expense-claims/{id}/reject
    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = RoleSets.HRStaffOrFinance)]
    public async Task<ActionResult<ApiResponse<ExpenseClaimDto>>> Reject(int id, [FromBody] ReasonRequest req)
    {
        try
        {
            var rejectedById = RequireEmployeeId();
            var result = await _service.RejectAsync(id, rejectedById, req.Reason);
            return Ok(ApiResponse<ExpenseClaimDto>.Ok(result, "Claim rejected."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
    }

    // POST /api/expense-claims/{id}/paid
    [HttpPost("{id:int}/paid")]
    [Authorize(Roles = RoleSets.SeniorHROrFinance)]
    public async Task<ActionResult<ApiResponse<ExpenseClaimDto>>> MarkPaid(int id, [FromBody] ReferenceRequest req)
    {
        try
        {
            var result = await _service.MarkPaidAsync(id, req.Reference);
            return Ok(ApiResponse<ExpenseClaimDto>.Ok(result, "Claim marked as paid."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<ExpenseClaimDto>.Fail(ex.Message)); }
    }

    private int RequireEmployeeId() =>
        _currentUser.EmployeeId ?? throw new InvalidOperationException("No employee profile linked to current user.");
}

public class SubmitExpenseClaimRequest
{
    public ExpenseCategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? ReceiptPath { get; set; }
    public string? Notes { get; set; }
}

public class ApproveExpenseRequest
{
    public decimal ApprovedAmount { get; set; }
}
