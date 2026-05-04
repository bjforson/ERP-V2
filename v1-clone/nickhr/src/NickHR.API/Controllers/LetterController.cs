using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Entities.System;
using NickHR.Core.Interfaces;
using NickHR.Services.Letter;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.HRStaff)]
public class LetterController : ControllerBase
{
    private readonly LetterService _letterService;
    private readonly ICurrentUserService _currentUser;

    public LetterController(LetterService letterService, ICurrentUserService currentUser)
    {
        _letterService = letterService;
        _currentUser = currentUser;
    }

    [HttpGet("templates")]
    public async Task<IActionResult> GetTemplates()
    {
        var templates = await _letterService.GetTemplatesAsync();
        return Ok(ApiResponse<object>.Ok(templates));
    }

    [HttpPost("templates")]
    public async Task<IActionResult> CreateTemplate([FromBody] LetterTemplate template)
    {
        var result = await _letterService.CreateTemplateAsync(template);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("preview/{templateId}/{employeeId}")]
    public async Task<IActionResult> Preview(int templateId, int employeeId)
    {
        // Even though the controller-level [Authorize] limits this to HR roles,
        // double-check the per-employee scope so future relaxations of the
        // class policy can't quietly leak letter content (which embeds PII).
        if (!await _currentUser.CanAccessEmployeeAsync(employeeId,
                "SuperAdmin", "HRManager", "HROfficer"))
        {
            return Forbid();
        }

        try
        {
            var html = await _letterService.GeneratePreviewAsync(templateId, employeeId);
            return Ok(ApiResponse<object>.Ok(new { html }));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    [HttpPost("generate/{templateId}/{employeeId}")]
    public async Task<IActionResult> Generate(int templateId, int employeeId, [FromQuery] int? generatedById)
    {
        try
        {
            var pdf = await _letterService.GeneratePdfAsync(templateId, employeeId, generatedById);
            return File(pdf, "application/pdf", $"Letter_{templateId}_{employeeId}.pdf");
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    [HttpGet("generated")]
    public async Task<IActionResult> GetGenerated([FromQuery] int? employeeId)
    {
        var letters = await _letterService.GetGeneratedLettersAsync(employeeId);
        return Ok(ApiResponse<object>.Ok(letters));
    }

    [HttpPost("seed-templates")]
    public async Task<IActionResult> SeedTemplates()
    {
        await _letterService.SeedDefaultTemplatesAsync();
        return Ok(ApiResponse<object>.Ok(null, "Default templates seeded."));
    }
}
