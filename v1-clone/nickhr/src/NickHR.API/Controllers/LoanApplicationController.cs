using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/loan-applications")]
[Authorize]
public class LoanApplicationController : ControllerBase
{
    private readonly ILoanApplicationService _service;
    private readonly ICurrentUserService _currentUser;

    public LoanApplicationController(ILoanApplicationService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    // POST /api/loan-applications/apply
    [HttpPost("apply")]
    public async Task<ActionResult<ApiResponse<LoanApplicationDto>>> Apply([FromBody] ApplyLoanRequest req)
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var result = await _service.ApplyForLoanAsync(
                employeeId, req.LoanType, req.Amount, req.Purpose,
                req.RepaymentMonths, req.GuarantorEmployeeId, req.DocumentPath);
            return Ok(ApiResponse<LoanApplicationDto>.Ok(result, "Loan application submitted successfully."));
        }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
    }

    // GET /api/loan-applications/my
    [HttpGet("my")]
    public async Task<ActionResult<ApiResponse<List<LoanApplicationDto>>>> GetMy()
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var result = await _service.GetMyApplicationsAsync(employeeId);
            return Ok(ApiResponse<List<LoanApplicationDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<LoanApplicationDto>>.Fail(ex.Message)); }
    }

    // GET /api/loan-applications/eligibility
    [HttpGet("eligibility")]
    public async Task<ActionResult<ApiResponse<LoanEligibilityDto>>> GetEligibility()
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var result = await _service.GetEligibilityAsync(employeeId);
            return Ok(ApiResponse<LoanEligibilityDto>.Ok(result));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<LoanEligibilityDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<LoanEligibilityDto>.Fail(ex.Message)); }
    }

    // GET /api/loan-applications/pending?status=
    [HttpGet("pending")]
    [Authorize(Roles = RoleSets.HRStaffPlusFinanceAndDept)]
    public async Task<ActionResult<ApiResponse<List<LoanApplicationDto>>>> GetPending([FromQuery] LoanApplicationStatus? status)
    {
        try
        {
            var result = await _service.GetPendingApprovalsAsync(status);
            return Ok(ApiResponse<List<LoanApplicationDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<LoanApplicationDto>>.Fail(ex.Message)); }
    }

    // POST /api/loan-applications/{id}/manager-approve
    [HttpPost("{id:int}/manager-approve")]
    [Authorize(Roles = RoleSets.AdminOrDeptManager)]
    public async Task<ActionResult<ApiResponse<LoanApplicationDto>>> ManagerApprove(int id)
    {
        try
        {
            var approverId = RequireEmployeeId();
            var result = await _service.ManagerApproveAsync(id, approverId);
            return Ok(ApiResponse<LoanApplicationDto>.Ok(result, "Manager approved."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
    }

    // POST /api/loan-applications/{id}/hr-approve
    [HttpPost("{id:int}/hr-approve")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<LoanApplicationDto>>> HRApprove(int id)
    {
        try
        {
            var approverId = RequireEmployeeId();
            var result = await _service.HRApproveAsync(id, approverId);
            return Ok(ApiResponse<LoanApplicationDto>.Ok(result, "HR approved."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
    }

    // POST /api/loan-applications/{id}/finance-approve
    [HttpPost("{id:int}/finance-approve")]
    [Authorize(Roles = RoleSets.AdminOrFinance)]
    public async Task<ActionResult<ApiResponse<LoanApplicationDto>>> FinanceApprove(int id)
    {
        try
        {
            var approverId = RequireEmployeeId();
            var result = await _service.FinanceApproveAsync(id, approverId);
            return Ok(ApiResponse<LoanApplicationDto>.Ok(result, "Finance approved. Loan created."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
    }

    // POST /api/loan-applications/{id}/reject
    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = RoleSets.HRStaffPlusFinanceAndDept)]
    public async Task<ActionResult<ApiResponse<LoanApplicationDto>>> Reject(int id, [FromBody] ReasonRequest body)
    {
        try
        {
            var rejectedById = RequireEmployeeId();
            var result = await _service.RejectAsync(id, rejectedById, body.Reason);
            return Ok(ApiResponse<LoanApplicationDto>.Ok(result, "Application rejected."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
    }

    // POST /api/loan-applications/{id}/disburse
    [HttpPost("{id:int}/disburse")]
    [Authorize(Roles = RoleSets.AdminOrFinance)]
    public async Task<ActionResult<ApiResponse<LoanApplicationDto>>> Disburse(int id, [FromBody] ReferenceRequest body)
    {
        try
        {
            var result = await _service.DisburseAsync(id, body.Reference);
            return Ok(ApiResponse<LoanApplicationDto>.Ok(result, "Loan disbursed."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<LoanApplicationDto>.Fail(ex.Message)); }
    }

    // -------------------------------------------------------------------------
    private int RequireEmployeeId() =>
        _currentUser.EmployeeId ?? throw new InvalidOperationException("No employee profile linked to current user.");
}

// ---- Request bodies --------------------------------------------------------
public class ApplyLoanRequest
{
    public LoanApplicationType LoanType { get; set; }
    public decimal Amount { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public int RepaymentMonths { get; set; }
    public int? GuarantorEmployeeId { get; set; }
    public string? DocumentPath { get; set; }
}

public class ReasonRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class ReferenceRequest
{
    public string Reference { get; set; } = string.Empty;
}
