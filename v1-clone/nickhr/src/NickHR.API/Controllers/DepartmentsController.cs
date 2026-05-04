using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.DTOs.Department;
using NickHR.Core.Interfaces;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DepartmentsController : ControllerBase
{
    private readonly IDepartmentService _departmentService;

    public DepartmentsController(IDepartmentService departmentService)
    {
        _departmentService = departmentService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<DepartmentDto>>>> GetAll()
    {
        var departments = await _departmentService.GetAllAsync();
        return Ok(ApiResponse<List<DepartmentDto>>.Ok(departments));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<DepartmentDto>>> GetById(int id)
    {
        var department = await _departmentService.GetByIdAsync(id);
        if (department is null)
            return NotFound(ApiResponse<DepartmentDto>.Fail($"Department with id {id} not found."));

        return Ok(ApiResponse<DepartmentDto>.Ok(department));
    }

    [HttpPost]
    [Authorize(Roles = RoleSets.SeniorHR)]
    public async Task<ActionResult<ApiResponse<DepartmentDto>>> Create([FromBody] CreateDepartmentDto dto)
    {
        try
        {
            var department = await _departmentService.CreateAsync(dto);
            return CreatedAtAction(nameof(GetById), new { id = department.Id },
                ApiResponse<DepartmentDto>.Ok(department, "Department created successfully."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<DepartmentDto>.Fail(ex.Message));
        }
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = RoleSets.SeniorHR)]
    public async Task<ActionResult<ApiResponse<DepartmentDto>>> Update(int id, [FromBody] CreateDepartmentDto dto)
    {
        try
        {
            var department = await _departmentService.UpdateAsync(id, dto);
            return Ok(ApiResponse<DepartmentDto>.Ok(department, "Department updated successfully."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<DepartmentDto>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<DepartmentDto>.Fail(ex.Message));
        }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = RoleSets.SeniorHR)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(int id)
    {
        try
        {
            await _departmentService.DeleteAsync(id);
            return Ok(ApiResponse<object>.Ok(null, "Department deleted successfully."));
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

    [HttpGet("org-chart")]
    public async Task<ActionResult<ApiResponse<List<OrgChartNodeDto>>>> GetOrgChart()
    {
        var chart = await _departmentService.GetOrgChartAsync();
        return Ok(ApiResponse<List<OrgChartNodeDto>>.Ok(chart));
    }
}
