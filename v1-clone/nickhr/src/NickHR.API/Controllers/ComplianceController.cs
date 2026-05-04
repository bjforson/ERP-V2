using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Entities.System;
using NickHR.Services.Compliance;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.SeniorHROrPayroll)]
public class ComplianceController : ControllerBase
{
    private readonly ComplianceService _complianceService;

    public ComplianceController(ComplianceService complianceService)
    {
        _complianceService = complianceService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var items = await _complianceService.GetAllAsync();
        return Ok(ApiResponse<object>.Ok(items));
    }

    [HttpGet("upcoming")]
    public async Task<IActionResult> GetUpcoming([FromQuery] int days = 90)
    {
        var items = await _complianceService.GetUpcomingAsync(days);
        return Ok(ApiResponse<object>.Ok(items));
    }

    [HttpPost("{id}/complete")]
    public async Task<IActionResult> MarkComplete(int id, [FromQuery] int completedById)
    {
        await _complianceService.MarkCompleteAsync(id, completedById);
        return Ok(ApiResponse<object>.Ok(null, "Marked as complete."));
    }

    [HttpPost("seed/{year}")]
    public async Task<IActionResult> SeedDefaults(int year)
    {
        await _complianceService.SeedDefaultsAsync(year);
        return Ok(ApiResponse<object>.Ok(null, "Defaults seeded."));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ComplianceDeadline deadline)
    {
        var result = await _complianceService.CreateAsync(deadline);
        return Ok(ApiResponse<object>.Ok(result));
    }
}
