using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Entities.Core;
using NickHR.Services.Travel;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TravelController : ControllerBase
{
    private readonly TravelService _travelService;

    public TravelController(TravelService travelService)
    {
        _travelService = travelService;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] TravelRequest request)
    {
        var result = await _travelService.CreateAsync(request);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("my/{employeeId}")]
    public async Task<IActionResult> GetMyRequests(int employeeId)
    {
        var requests = await _travelService.GetMyRequestsAsync(employeeId);
        return Ok(ApiResponse<object>.Ok(requests));
    }

    [HttpGet("pending")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> GetPending()
    {
        var requests = await _travelService.GetPendingApprovalsAsync();
        return Ok(ApiResponse<object>.Ok(requests));
    }

    [HttpPost("{id}/approve")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> Approve(int id, [FromQuery] int approvedById)
    {
        await _travelService.ApproveAsync(id, approvedById);
        return Ok(ApiResponse<object>.Ok(null, "Approved."));
    }

    [HttpPost("{id}/reject")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> Reject(int id, [FromQuery] int approvedById)
    {
        await _travelService.RejectAsync(id, approvedById);
        return Ok(ApiResponse<object>.Ok(null, "Rejected."));
    }

    [HttpPost("{id}/reconcile")]
    public async Task<IActionResult> Reconcile(int id, [FromBody] ReconcileRequest req)
    {
        await _travelService.ReconcileAsync(id, req.ActualCost, req.Notes);
        return Ok(ApiResponse<object>.Ok(null, "Reconciled."));
    }
}

public class ReconcileRequest
{
    public decimal ActualCost { get; set; }
    public string? Notes { get; set; }
}
