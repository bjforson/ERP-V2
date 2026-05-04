using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/medical-claims")]
[Authorize]
public class MedicalClaimController : ControllerBase
{
    private readonly IMedicalClaimService _service;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;

    public MedicalClaimController(
        IMedicalClaimService service,
        ICurrentUserService currentUser,
        IAuditService audit)
    {
        _service = service;
        _currentUser = currentUser;
        _audit = audit;
    }

    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    // POST /api/medical-claims/submit
    [HttpPost("submit")]
    public async Task<ActionResult<ApiResponse<MedicalClaimDto>>> Submit([FromBody] SubmitMedicalClaimRequest req)
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var result = await _service.SubmitClaimAsync(
                employeeId, req.Category, req.Description, req.ProviderName,
                req.ReceiptDate, req.ClaimAmount, req.ReceiptPaths);
            return Ok(ApiResponse<MedicalClaimDto>.Ok(result, "Medical claim submitted successfully."));
        }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
    }

    // GET /api/medical-claims/my?year=
    [HttpGet("my")]
    public async Task<ActionResult<ApiResponse<MyMedicalClaimsResponse>>> GetMy([FromQuery] int? year)
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var (claims, balance) = await _service.GetMyClaimsAsync(employeeId, year);

            // PHI access — log self-views so a later anomaly (e.g. someone querying
            // medical history minutes before a hostile termination) is reconstructable.
            await _audit.LogAsync(
                userId: _currentUser.UserId,
                action: "MedicalClaims.ViewSelf",
                entityType: "MedicalClaims",
                entityId: employeeId.ToString(),
                oldValues: null,
                newValues: $"year={year}, claimCount={claims?.Count ?? 0}",
                ipAddress: RemoteIp);

            return Ok(ApiResponse<MyMedicalClaimsResponse>.Ok(new MyMedicalClaimsResponse(claims, balance)));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<MyMedicalClaimsResponse>.Fail(ex.Message)); }
    }

    // GET /api/medical-claims/balance
    [HttpGet("balance")]
    public async Task<ActionResult<ApiResponse<MedicalBalanceDto>>> GetBalance([FromQuery] int? year)
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var targetYear = year ?? DateTime.UtcNow.Year;
            var result = await _service.GetEmployeeBalanceAsync(employeeId, targetYear);
            return Ok(ApiResponse<MedicalBalanceDto>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<MedicalBalanceDto>.Fail(ex.Message)); }
    }

    // GET /api/medical-claims/review?status=
    [HttpGet("review")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<List<MedicalClaimDto>>>> GetForReview([FromQuery] MedicalClaimStatus? status)
    {
        try
        {
            var result = await _service.GetClaimsForReviewAsync(status);

            // PHI access by HR/admin — every list view is recorded so misuse can be
            // reviewed (e.g. an HR officer scanning sensitive claims unrelated to a case).
            await _audit.LogAsync(
                userId: _currentUser.UserId,
                action: "MedicalClaims.ViewForReview",
                entityType: "MedicalClaims",
                entityId: status?.ToString() ?? "all",
                oldValues: null,
                newValues: $"resultCount={result?.Count ?? 0}",
                ipAddress: RemoteIp);

            return Ok(ApiResponse<List<MedicalClaimDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<MedicalClaimDto>>.Fail(ex.Message)); }
    }

    // POST /api/medical-claims/{id}/review
    [HttpPost("{id:int}/review")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<MedicalClaimDto>>> Review(int id)
    {
        try
        {
            var reviewerId = RequireEmployeeId();
            var result = await _service.ReviewClaimAsync(id, reviewerId);
            return Ok(ApiResponse<MedicalClaimDto>.Ok(result, "Claim under review."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
    }

    // POST /api/medical-claims/{id}/approve
    [HttpPost("{id:int}/approve")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<MedicalClaimDto>>> Approve(int id, [FromBody] ApproveMedicalClaimRequest body)
    {
        try
        {
            var approverId = RequireEmployeeId();
            var result = await _service.ApproveClaimAsync(id, approverId, body.ApprovedAmount, body.PaymentMethod);
            return Ok(ApiResponse<MedicalClaimDto>.Ok(result, "Claim approved."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
    }

    // POST /api/medical-claims/{id}/reject
    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<MedicalClaimDto>>> Reject(int id, [FromBody] ReasonRequest body)
    {
        try
        {
            var rejectedById = RequireEmployeeId();
            var result = await _service.RejectClaimAsync(id, rejectedById, body.Reason);
            return Ok(ApiResponse<MedicalClaimDto>.Ok(result, "Claim rejected."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
    }

    // POST /api/medical-claims/{id}/paid
    [HttpPost("{id:int}/paid")]
    [Authorize(Roles = RoleSets.HRStaffOrFinance)]
    public async Task<ActionResult<ApiResponse<MedicalClaimDto>>> MarkPaid(int id, [FromBody] ReferenceRequest body)
    {
        try
        {
            var result = await _service.MarkAsPaidAsync(id, body.Reference);
            return Ok(ApiResponse<MedicalClaimDto>.Ok(result, "Claim marked as paid."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<MedicalClaimDto>.Fail(ex.Message)); }
    }

    // GET /api/medical-claims/policy
    [HttpGet("policy")]
    public async Task<ActionResult<ApiResponse<MedicalBenefitDto?>>> GetPolicy()
    {
        try
        {
            var result = await _service.GetBenefitPolicyAsync();
            return Ok(ApiResponse<MedicalBenefitDto?>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<MedicalBenefitDto?>.Fail(ex.Message)); }
    }

    // PUT /api/medical-claims/policy
    [HttpPut("policy")]
    [Authorize(Roles = RoleSets.SuperAdminOnly)]
    public async Task<ActionResult<ApiResponse<MedicalBenefitDto>>> UpdatePolicy([FromBody] UpdateMedicalPolicyRequest req)
    {
        try
        {
            var result = await _service.UpdateBenefitPolicyAsync(
                req.Name, req.AnnualLimit, req.WaitingPeriodMonths, req.CoversDependents);
            return Ok(ApiResponse<MedicalBenefitDto>.Ok(result, "Policy updated."));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<MedicalBenefitDto>.Fail(ex.Message)); }
    }

    // -------------------------------------------------------------------------
    private int RequireEmployeeId() =>
        _currentUser.EmployeeId ?? throw new InvalidOperationException("No employee profile linked to current user.");
}

// ---- Request / response bodies --------------------------------------------
public class SubmitMedicalClaimRequest
{
    public NickHR.Core.Enums.MedicalClaimCategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public DateTime? ReceiptDate { get; set; }
    public decimal ClaimAmount { get; set; }
    public string? ReceiptPaths { get; set; }
}

public class ApproveMedicalClaimRequest
{
    public decimal ApprovedAmount { get; set; }
    public NickHR.Core.Enums.MedicalPaymentMethod PaymentMethod { get; set; }
}

public class UpdateMedicalPolicyRequest
{
    public string Name { get; set; } = string.Empty;
    public decimal AnnualLimit { get; set; }
    public int WaitingPeriodMonths { get; set; }
    public bool CoversDependents { get; set; }
}

public record MyMedicalClaimsResponse(
    List<MedicalClaimDto> Claims,
    MedicalBalanceDto Balance);
