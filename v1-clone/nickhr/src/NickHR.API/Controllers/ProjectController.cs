using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Entities.Core;
using NickHR.Services.Project;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProjectController : ControllerBase
{
    private readonly ProjectService _projectService;

    public ProjectController(ProjectService projectService)
    {
        _projectService = projectService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var projects = await _projectService.GetAllProjectsAsync();
        return Ok(ApiResponse<object>.Ok(projects));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var project = await _projectService.GetProjectByIdAsync(id);
        if (project == null) return NotFound();
        return Ok(ApiResponse<object>.Ok(project));
    }

    [HttpPost]
    [Authorize(Roles = RoleSets.SeniorHROrDeptManager)]
    public async Task<IActionResult> Create([FromBody] Core.Entities.Core.Project project)
    {
        var result = await _projectService.CreateProjectAsync(project);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("timesheets/{employeeId}")]
    public async Task<IActionResult> GetTimesheets(int employeeId, [FromQuery] DateTime weekStart)
    {
        var entries = await _projectService.GetTimesheetsAsync(employeeId, weekStart);
        return Ok(ApiResponse<object>.Ok(entries));
    }

    [HttpPost("timesheets")]
    public async Task<IActionResult> CreateTimesheet([FromBody] TimesheetEntry entry)
    {
        var result = await _projectService.CreateTimesheetEntryAsync(entry);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpPost("timesheets/submit")]
    public async Task<IActionResult> SubmitWeek([FromQuery] int employeeId, [FromQuery] DateTime weekStart)
    {
        await _projectService.SubmitWeekAsync(employeeId, weekStart);
        return Ok(ApiResponse<object>.Ok(null, "Week submitted."));
    }
}
