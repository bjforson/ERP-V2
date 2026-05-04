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
public class LocationsController : ControllerBase
{
    private readonly NickHRDbContext _db;

    public LocationsController(NickHRDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<Location>>>> GetAll()
    {
        var locations = await _db.Locations
            .OrderBy(l => l.Name)
            .ToListAsync();
        return Ok(ApiResponse<List<Location>>.Ok(locations));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<Location>>> GetById(int id)
    {
        var location = await _db.Locations.FindAsync(id);
        if (location is null)
            return NotFound(ApiResponse<Location>.Fail($"Location with id {id} not found."));

        return Ok(ApiResponse<Location>.Ok(location));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<Location>>> Create([FromBody] Location location)
    {
        location.Id = 0;
        location.CreatedAt = DateTime.UtcNow;
        _db.Locations.Add(location);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = location.Id },
            ApiResponse<Location>.Ok(location, "Location created successfully."));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse<Location>>> Update(int id, [FromBody] Location dto)
    {
        var location = await _db.Locations.FindAsync(id);
        if (location is null)
            return NotFound(ApiResponse<Location>.Fail($"Location with id {id} not found."));

        location.Name = dto.Name;
        location.Address = dto.Address;
        location.City = dto.City;
        location.Region = dto.Region;
        location.Country = dto.Country;
        location.IsHeadOffice = dto.IsHeadOffice;
        location.IsActive = dto.IsActive;
        location.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(ApiResponse<Location>.Ok(location, "Location updated successfully."));
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(int id)
    {
        var location = await _db.Locations.FindAsync(id);
        if (location is null)
            return NotFound(ApiResponse<object>.Fail($"Location with id {id} not found."));

        location.IsDeleted = true;
        location.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Ok(null, "Location deleted successfully."));
    }
}
