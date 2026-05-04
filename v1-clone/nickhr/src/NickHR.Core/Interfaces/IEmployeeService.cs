using NickHR.Core.DTOs;
using NickHR.Core.DTOs.Department;
using NickHR.Core.DTOs.Employee;

namespace NickHR.Core.Interfaces;

public interface IEmployeeService
{
    Task<EmployeeDetailDto?> GetByIdAsync(int id);
    Task<PagedResult<EmployeeListDto>> GetListAsync(EmployeeSearchFilter filter);
    Task<EmployeeDetailDto> CreateAsync(CreateEmployeeDto dto);
    Task<EmployeeDetailDto> UpdateAsync(int id, UpdateEmployeeDto dto);
    Task DeleteAsync(int id);
    Task<EmployeeDetailDto?> GetByCodeAsync(string employeeCode);
    Task<List<OrgChartNodeDto>> GetOrgChartAsync();
}
