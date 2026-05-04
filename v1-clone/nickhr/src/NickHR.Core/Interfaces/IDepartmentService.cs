using NickHR.Core.DTOs.Department;

namespace NickHR.Core.Interfaces;

public interface IDepartmentService
{
    Task<DepartmentDto?> GetByIdAsync(int id);
    Task<List<DepartmentDto>> GetAllAsync();
    Task<DepartmentDto> CreateAsync(CreateDepartmentDto dto);
    Task<DepartmentDto> UpdateAsync(int id, CreateDepartmentDto dto);
    Task DeleteAsync(int id);
    Task<List<OrgChartNodeDto>> GetOrgChartAsync();
}
