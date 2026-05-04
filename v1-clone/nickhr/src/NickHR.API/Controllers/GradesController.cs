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
public class GradesController : ControllerBase
{
    private readonly NickHRDbContext _db;

    public GradesController(NickHRDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<Grade>>>> GetAll()
    {
        var grades = await _db.Grades
            .OrderBy(g => g.Level)
            .ToListAsync();
        return Ok(ApiResponse<List<Grade>>.Ok(grades));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<Grade>>> GetById(int id)
    {
        var grade = await _db.Grades.FindAsync(id);
        if (grade is null)
            return NotFound(ApiResponse<Grade>.Fail($"Grade with id {id} not found."));

        return Ok(ApiResponse<Grade>.Ok(grade));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<Grade>>> Create([FromBody] Grade grade)
    {
        grade.Id = 0;
        grade.CreatedAt = DateTime.UtcNow;
        _db.Grades.Add(grade);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = grade.Id },
            ApiResponse<Grade>.Ok(grade, "Grade created successfully."));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse<Grade>>> Update(int id, [FromBody] Grade dto)
    {
        var grade = await _db.Grades.FindAsync(id);
        if (grade is null)
            return NotFound(ApiResponse<Grade>.Fail($"Grade with id {id} not found."));

        grade.Name = dto.Name;
        grade.Level = dto.Level;
        grade.MinSalary = dto.MinSalary;
        grade.MidSalary = dto.MidSalary;
        grade.MaxSalary = dto.MaxSalary;
        grade.Description = dto.Description;
        grade.IsActive = dto.IsActive;
        grade.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(ApiResponse<Grade>.Ok(grade, "Grade updated successfully."));
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(int id)
    {
        var grade = await _db.Grades.FindAsync(id);
        if (grade is null)
            return NotFound(ApiResponse<object>.Fail($"Grade with id {id} not found."));

        grade.IsDeleted = true;
        grade.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(null, "Grade deleted successfully."));
    }
}
