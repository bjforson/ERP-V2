using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Interfaces;
using NickHR.Services.Announcement;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AnnouncementController : ControllerBase
{
    private readonly IAnnouncementService _announcementService;
    private readonly ICurrentUserService _currentUser;

    public AnnouncementController(IAnnouncementService announcementService, ICurrentUserService currentUser)
    {
        _announcementService = announcementService;
        _currentUser = currentUser;
    }

    /// <summary>Create a new announcement.</summary>
    [HttpPost]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> Create([FromBody] CreateAnnouncementDto dto)
    {
        try
        {
            var authorId = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var announcement = await _announcementService.CreateAsync(
                dto.Title, dto.Content, dto.DepartmentId, authorId, dto.ExpiresAt);

            return Ok(ApiResponse<object>.Ok(announcement, "Announcement created."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get active announcements (optionally filtered by department).</summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActive([FromQuery] int? departmentId = null)
    {
        var announcements = await _announcementService.GetActiveAsync(departmentId);
        return Ok(ApiResponse<object>.Ok(announcements.Select(a => new
        {
            a.Id,
            a.Title,
            a.Content,
            a.PublishedAt,
            a.ExpiresAt,
            a.DepartmentId,
            Department = a.Department?.Name,
            Author = $"{a.Author.FirstName} {a.Author.LastName}"
        })));
    }

    /// <summary>Get all announcements (admin view).</summary>
    [HttpGet]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> GetAll()
    {
        var announcements = await _announcementService.GetAllAsync();
        return Ok(ApiResponse<object>.Ok(announcements));
    }

    /// <summary>Update an announcement.</summary>
    [HttpPut("{id:int}")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAnnouncementDto dto)
    {
        try
        {
            var announcement = await _announcementService.UpdateAsync(
                id, dto.Title, dto.Content, dto.DepartmentId, dto.ExpiresAt);
            return Ok(ApiResponse<object>.Ok(announcement, "Announcement updated."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Delete (soft-delete) an announcement.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            await _announcementService.DeleteAsync(id);
            return Ok(ApiResponse<object>.Ok(null, "Announcement deleted."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }
}

public class CreateAnnouncementDto
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int? DepartmentId { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class UpdateAnnouncementDto
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int? DepartmentId { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
