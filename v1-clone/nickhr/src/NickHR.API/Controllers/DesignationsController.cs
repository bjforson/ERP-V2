using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NickHR.Core.DTOs;
using NickHR.Core.Entities.Core;
using NickHR.Infrastructure.Data;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.SeniorHR)]
public class DesignationsController : ControllerBase
{
    private readonly NickHRDbContext _db;

    public DesignationsController(NickHRDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<Designation>>>> GetAll()
    {
        var designations = await _db.Designations
            .Include(d => d.Grade)
            .OrderBy(d => d.Title)
            .ToListAsync();
        return Ok(ApiResponse<List<Designation>>.Ok(designations));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<Designation>>> GetById(int id)
    {
        var designation = await _db.Designations
            .Include(d => d.Grade)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (designation is null)
            return NotFound(ApiResponse<Designation>.Fail($"Designation with id {id} not found."));

        return Ok(ApiResponse<Designation>.Ok(designation));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<Designation>>> Create([FromBody] Designation designation)
    {
        designation.Id = 0;
        designation.CreatedAt = DateTime.UtcNow;
        _db.Designations.Add(designation);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = designation.Id },
            ApiResponse<Designation>.Ok(designation, "Designation created successfully."));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse<Designation>>> Update(int id, [FromBody] Designation dto)
    {
        var designation = await _db.Designations.FindAsync(id);
        if (designation is null)
            return NotFound(ApiResponse<Designation>.Fail($"Designation with id {id} not found."));

        designation.Title = dto.Title;
        designation.Code = dto.Code;
        designation.Description = dto.Description;
        designation.GradeId = dto.GradeId;
        designation.IsActive = dto.IsActive;
        designation.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(ApiResponse<Designation>.Ok(designation, "Designation updated successfully."));
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(int id)
    {
        var designation = await _db.Designations.FindAsync(id);
        if (designation is null)
            return NotFound(ApiResponse<object>.Fail($"Designation with id {id} not found."));

        designation.IsDeleted = true;
        designation.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(null, "Designation deleted successfully."));
    }
}
