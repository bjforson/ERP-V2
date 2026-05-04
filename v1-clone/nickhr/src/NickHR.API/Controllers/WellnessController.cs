using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Services.Common;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
public class WellnessController : ControllerBase
{
    private readonly WellnessService _service;

    public WellnessController(WellnessService service)
    {
        _service = service;
    }

    /// <summary>Get company-wide wellness metrics dashboard.</summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> GetMetrics()
    {
        var metrics = await _service.GetWellnessMetricsAsync();
        return Ok(ApiResponse<WellnessMetricsDto>.Ok(metrics));
    }

    /// <summary>Get wellness metrics for a specific employee.</summary>
    [HttpGet("employee/{employeeId}")]
    public async Task<IActionResult> GetEmployeeWellness(int employeeId)
    {
        var metrics = await _service.GetEmployeeWellnessAsync(employeeId);
        return Ok(ApiResponse<EmployeeWellnessDto>.Ok(metrics));
    }
}
