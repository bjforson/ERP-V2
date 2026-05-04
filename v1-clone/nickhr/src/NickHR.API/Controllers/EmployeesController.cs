using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.DTOs.Department;
using NickHR.Core.DTOs.Employee;
using NickHR.Core.Interfaces;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EmployeesController : ControllerBase
{
    private readonly IEmployeeService _employeeService;
    private readonly ICurrentUserService _currentUser;

    public EmployeesController(IEmployeeService employeeService, ICurrentUserService currentUser)
    {
        _employeeService = employeeService;
        _currentUser = currentUser;
    }

    [HttpGet]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<PagedResult<EmployeeListDto>>>> GetList(
        [FromQuery] EmployeeSearchFilter filter)
    {
        var result = await _employeeService.GetListAsync(filter);
        return Ok(ApiResponse<PagedResult<EmployeeListDto>>.Ok(result));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<EmployeeDetailDto>>> GetById(int id)
    {
        // Class-level [Authorize] permits any authenticated user, so without an
        // explicit per-employee scope a regular employee could enumerate every
        // record by id. Self-access OR HR/admin role required.
        if (!await _currentUser.CanAccessEmployeeAsync(id,
                "SuperAdmin", "HRManager", "HROfficer"))
        {
            return Forbid();
        }

        var employee = await _employeeService.GetByIdAsync(id);
        if (employee is null)
            return NotFound(ApiResponse<EmployeeDetailDto>.Fail($"Employee with id {id} not found."));

        return Ok(ApiResponse<EmployeeDetailDto>.Ok(employee));
    }

    [HttpGet("code/{code}")]
    public async Task<ActionResult<ApiResponse<EmployeeDetailDto>>> GetByCode(string code)
    {
        var employee = await _employeeService.GetByCodeAsync(code);
        if (employee is null)
            return NotFound(ApiResponse<EmployeeDetailDto>.Fail($"Employee with code '{code}' not found."));

        return Ok(ApiResponse<EmployeeDetailDto>.Ok(employee));
    }

    [HttpPost]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<EmployeeDetailDto>>> Create([FromBody] CreateEmployeeDto dto)
    {
        try
        {
            var employee = await _employeeService.CreateAsync(dto);
            return CreatedAtAction(nameof(GetById), new { id = employee.Id },
                ApiResponse<EmployeeDetailDto>.Ok(employee, "Employee created successfully."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<EmployeeDetailDto>.Fail(ex.Message));
        }
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<ActionResult<ApiResponse<EmployeeDetailDto>>> Update(int id, [FromBody] UpdateEmployeeDto dto)
    {
        try
        {
            var employee = await _employeeService.UpdateAsync(id, dto);
            return Ok(ApiResponse<EmployeeDetailDto>.Ok(employee, "Employee updated successfully."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<EmployeeDetailDto>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<EmployeeDetailDto>.Fail(ex.Message));
        }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = RoleSets.SeniorHR)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(int id)
    {
        try
        {
            await _employeeService.DeleteAsync(id);
            return Ok(ApiResponse<object>.Ok(null, "Employee deleted successfully."));
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
        var chart = await _employeeService.GetOrgChartAsync();
        return Ok(ApiResponse<List<OrgChartNodeDto>>.Ok(chart));
    }

    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<EmployeeDetailDto>>> GetMyProfile()
    {
        var employeeId = _currentUser.EmployeeId;
        if (employeeId is null)
            return NotFound(ApiResponse<EmployeeDetailDto>.Fail("No employee profile linked to current user."));

        var employee = await _employeeService.GetByIdAsync(employeeId.Value);
        if (employee is null)
            return NotFound(ApiResponse<EmployeeDetailDto>.Fail("Employee profile not found."));

        return Ok(ApiResponse<EmployeeDetailDto>.Ok(employee));
    }
}
