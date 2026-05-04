using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Interfaces;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/policies")]
[Authorize]
public class PolicyDocumentController : ControllerBase
{
    private readonly IPolicyDocumentService _service;
    private readonly ICurrentUserService _currentUser;

    public PolicyDocumentController(IPolicyDocumentService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    // GET /api/policies
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<PolicyDocumentDto>>>> GetAll()
    {
        try
        {
            var result = await _service.GetAllAsync();
            return Ok(ApiResponse<List<PolicyDocumentDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<PolicyDocumentDto>>.Fail(ex.Message)); }
    }

    // GET /api/policies/active
    [HttpGet("active")]
    public async Task<ActionResult<ApiResponse<List<PolicyDocumentDto>>>> GetActive()
    {
        try
        {
            var result = await _service.GetActiveAsync();
            return Ok(ApiResponse<List<PolicyDocumentDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<PolicyDocumentDto>>.Fail(ex.Message)); }
    }

    // GET /api/policies/{id}
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<PolicyDocumentDto>>> GetById(int id)
    {
        try
        {
            var result = await _service.GetByIdAsync(id);
            if (result == null) return NotFound(ApiResponse<PolicyDocumentDto>.Fail("Policy not found."));
            return Ok(ApiResponse<PolicyDocumentDto>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<PolicyDocumentDto>.Fail(ex.Message)); }
    }

    // POST /api/policies
    [HttpPost]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<PolicyDocumentDto>>> Create([FromBody] CreatePolicyDocumentRequest req)
    {
        try
        {
            var result = await _service.CreateAsync(req.Title, req.Category, req.Version, req.EffectiveDate, req.Description, req.FilePath, req.RequiresAcknowledgement);
            return Ok(ApiResponse<PolicyDocumentDto>.Ok(result, "Policy document created."));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<PolicyDocumentDto>.Fail(ex.Message)); }
    }

    // PUT /api/policies/{id}
    [HttpPut("{id:int}")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<PolicyDocumentDto>>> Update(int id, [FromBody] UpdatePolicyDocumentRequest req)
    {
        try
        {
            var result = await _service.UpdateAsync(id, req.Title, req.Category, req.Version, req.EffectiveDate, req.Description, req.FilePath, req.RequiresAcknowledgement, req.IsActive);
            return Ok(ApiResponse<PolicyDocumentDto>.Ok(result, "Policy document updated."));
        }
        catch (KeyNotFoundException ex) { return NotFound(ApiResponse<PolicyDocumentDto>.Fail(ex.Message)); }
        catch (Exception ex) { return BadRequest(ApiResponse<PolicyDocumentDto>.Fail(ex.Message)); }
    }

    // POST /api/policies/{id}/acknowledge
    [HttpPost("{id:int}/acknowledge")]
    public async Task<ActionResult<ApiResponse<string>>> Acknowledge(int id)
    {
        try
        {
            var employeeId = RequireEmployeeId();
            await _service.AcknowledgeAsync(id, employeeId);
            return Ok(ApiResponse<string>.Ok("Acknowledged", "Policy acknowledged."));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<string>.Fail(ex.Message)); }
    }

    // GET /api/policies/{id}/acknowledgements
    [HttpGet("{id:int}/acknowledgements")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<List<PolicyAcknowledgementDto>>>> GetAcknowledgements(int id)
    {
        try
        {
            var result = await _service.GetAcknowledgementStatusAsync(id);
            return Ok(ApiResponse<List<PolicyAcknowledgementDto>>.Ok(result));
        }
        catch (Exception ex) { return BadRequest(ApiResponse<List<PolicyAcknowledgementDto>>.Fail(ex.Message)); }
    }

    private int RequireEmployeeId() =>
        _currentUser.EmployeeId ?? throw new InvalidOperationException("No employee profile linked to current user.");
}

public class CreatePolicyDocumentRequest
{
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "Company Policy";
    public string Version { get; set; } = "1.0";
    public DateTime EffectiveDate { get; set; }
    public string? Description { get; set; }
    public string? FilePath { get; set; }
    public bool RequiresAcknowledgement { get; set; }
}

public class UpdatePolicyDocumentRequest : CreatePolicyDocumentRequest
{
    public bool IsActive { get; set; } = true;
}
