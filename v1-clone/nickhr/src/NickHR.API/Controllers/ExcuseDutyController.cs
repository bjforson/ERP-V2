using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/excuse-duty")]
[Authorize]
public class ExcuseDutyController : ControllerBase
{
    private readonly IExcuseDutyService _service;
    private readonly ICurrentUserService _currentUser;

    public ExcuseDutyController(IExcuseDutyService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    // POST /api/excuse-duty/request
    [HttpPost("request")]
    public async Task<ActionResult<ApiResponse<ExcuseDutyDto>>> SubmitRequest([FromBody] RequestExcuseDutyRequest req)
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var result = await _service.RequestExcuseDutyAsync(
                employeeId, req.Type, req.Date, req.StartTime, req.EndTime,
                req.Reason, req.Destination, req.MedicalCertPath);
            return Ok(ApiResponse<ExcuseDutyDto>.Ok(result, "Excuse duty request submitted."));
        }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
        catch (ArgumentException ex) { return BadRequest(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
    }

    // GET /api/excuse-duty/my?month=&year=
    [HttpGet("my")]
    public async Task<ActionResult<ApiResponse<List<ExcuseDutyDto>>>> GetMy(
        [FromQuery] int? month, [FromQuery] int? year)
    {
        try
        {
            var employeeId = RequireEmployeeId();
            var result = await _service.GetMyRequestsAsync(employeeId, month, year);
            return Ok(ApiResponse<List<ExcuseDutyDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<ExcuseDutyDto>>.Fail(ex.Message)); }
    }

    // GET /api/excuse-duty/pending
    [HttpGet("pending")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<ActionResult<ApiResponse<List<ExcuseDutyDto>>>> GetPending()
    {
        try
        {
            var result = await _service.GetPendingApprovalsAsync();
            return Ok(ApiResponse<List<ExcuseDutyDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<ExcuseDutyDto>>.Fail(ex.Message)); }
    }

    // POST /api/excuse-duty/{id}/approve
    [HttpPost("{id:int}/approve")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<ActionResult<ApiResponse<ExcuseDutyDto>>> Approve(int id)
    {
        try
        {
            var approverId = RequireEmployeeId();
            var result = await _service.ApproveAsync(id, approverId);
            return Ok(ApiResponse<ExcuseDutyDto>.Ok(result, "Excuse duty approved."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
    }

    // POST /api/excuse-duty/{id}/reject
    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<ActionResult<ApiResponse<ExcuseDutyDto>>> Reject(int id, [FromBody] ReasonRequest body)
    {
        try
        {
            var approverId = RequireEmployeeId();
            var result = await _service.RejectAsync(id, approverId, body.Reason);
            return Ok(ApiResponse<ExcuseDutyDto>.Ok(result, "Excuse duty rejected."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
    }

    // POST /api/excuse-duty/{id}/confirm-return
    [HttpPost("{id:int}/confirm-return")]
    public async Task<ActionResult<ApiResponse<ExcuseDutyDto>>> ConfirmReturn(int id, [FromBody] ConfirmReturnRequest body)
    {
        try
        {
            var result = await _service.ConfirmReturnAsync(id, body.ReturnTime);
            return Ok(ApiResponse<ExcuseDutyDto>.Ok(result, "Return confirmed."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return BadRequest(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<ExcuseDutyDto>.Fail(ex.Message)); }
    }

    // GET /api/excuse-duty/report?departmentId=&month=&year=
    [HttpGet("report")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<ActionResult<ApiResponse<List<ExcuseDutyMonthlyReportDto>>>> GetReport(
        [FromQuery] int? departmentId,
        [FromQuery] int? month,
        [FromQuery] int? year)
    {
        try
        {
            var targetMonth = month ?? DateTime.UtcNow.Month;
            var targetYear = year ?? DateTime.UtcNow.Year;
            var result = await _service.GetMonthlyReportAsync(departmentId, targetMonth, targetYear);
            return Ok(ApiResponse<List<ExcuseDutyMonthlyReportDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<ExcuseDutyMonthlyReportDto>>.Fail(ex.Message)); }
    }

    // -------------------------------------------------------------------------
    private int RequireEmployeeId() =>
        _currentUser.EmployeeId ?? throw new InvalidOperationException("No employee profile linked to current user.");
}

// ---- Request bodies --------------------------------------------------------
public class RequestExcuseDutyRequest
{
    public ExcuseDutyType Type { get; set; }
    public DateTime Date { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Destination { get; set; }
    public string? MedicalCertPath { get; set; }
}

public class ConfirmReturnRequest
{
    public TimeSpan ReturnTime { get; set; }
}
