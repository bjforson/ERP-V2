using Microsoft.EntityFrameworkCore;
using NickHR.Core.DTOs.Department;
using NickHR.Core.Entities.Core;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Department;

public class DepartmentService : IDepartmentService
{
    private readonly IRepository<Core.Entities.Core.Department> _departmentRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly NickHRDbContext _db;

    public DepartmentService(
        IRepository<Core.Entities.Core.Department> departmentRepo,
        IUnitOfWork unitOfWork,
        NickHRDbContext db)
    {
        _departmentRepo = departmentRepo;
        _unitOfWork = unitOfWork;
        _db = db;
    }

    public async Task<DepartmentDto?> GetByIdAsync(int id)
    {
        var dept = await _db.Departments
            .Include(d => d.ParentDepartment)
            .Include(d => d.HeadOfDepartment)
            .Include(d => d.Employees)
            .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);

        return dept is null ? null : MapToDto(dept);
    }

    public async Task<List<DepartmentDto>> GetAllAsync()
    {
        var departments = await _db.Departments
            .Include(d => d.ParentDepartment)
            .Include(d => d.HeadOfDepartment)
            .Include(d => d.Employees)
            .Where(d => !d.IsDeleted)
            .OrderBy(d => d.Name)
            .ToListAsync();

        return departments.Select(MapToDto).ToList();
    }

    public async Task<DepartmentDto> CreateAsync(CreateDepartmentDto dto)
    {
        var codeExists = await _departmentRepo.ExistsAsync(d => d.Code == dto.Code && !d.IsDeleted);
        if (codeExists)
            throw new InvalidOperationException($"Department with code '{dto.Code}' already exists.");

        var department = new Core.Entities.Core.Department
        {
            Name = dto.Name,
            Code = dto.Code,
            Description = dto.Description,
            ParentDepartmentId = dto.ParentDepartmentId,
            CostCenter = dto.CostCenter,
            HeadOfDepartmentId = dto.HeadOfDepartmentId,
            IsActive = true
        };

        await _departmentRepo.AddAsync(department);
        await _unitOfWork.SaveChangesAsync();

        return await GetByIdAsync(department.Id)
            ?? throw new InvalidOperationException("Failed to retrieve newly created department.");
    }

    public async Task<DepartmentDto> UpdateAsync(int id, CreateDepartmentDto dto)
    {
        var department = await _db.Departments.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted)
            ?? throw new KeyNotFoundException($"Department with ID {id} not found.");

        // Validate code uniqueness if it changed
        if (!string.Equals(department.Code, dto.Code, StringComparison.OrdinalIgnoreCase))
        {
            var codeExists = await _departmentRepo.ExistsAsync(d => d.Code == dto.Code && d.Id != id && !d.IsDeleted);
            if (codeExists)
                throw new InvalidOperationException($"Department with code '{dto.Code}' already exists.");
        }

        department.Name = dto.Name;
        department.Code = dto.Code;
        department.Description = dto.Description;
        department.ParentDepartmentId = dto.ParentDepartmentId;
        department.CostCenter = dto.CostCenter;
        department.HeadOfDepartmentId = dto.HeadOfDepartmentId;

        _departmentRepo.Update(department);
        await _unitOfWork.SaveChangesAsync();

        return await GetByIdAsync(department.Id)
            ?? throw new InvalidOperationException("Failed to retrieve updated department.");
    }

    public async Task DeleteAsync(int id)
    {
        var department = await _db.Departments
            .Include(d => d.Employees)
            .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted)
            ?? throw new KeyNotFoundException($"Department with ID {id} not found.");

        var activeEmployeeCount = department.Employees.Count(e => !e.IsDeleted);
        if (activeEmployeeCount > 0)
            throw new InvalidOperationException(
                $"Cannot delete department '{department.Name}' because it has {activeEmployeeCount} active employee(s) assigned.");

        _departmentRepo.SoftDelete(department);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<List<OrgChartNodeDto>> GetOrgChartAsync()
    {
        var departments = await _db.Departments
            .Include(d => d.HeadOfDepartment)
            .Include(d => d.Employees)
            .Where(d => !d.IsDeleted && d.IsActive)
            .OrderBy(d => d.Name)
            .ToListAsync();

        var nodes = departments.Select(d => new OrgChartNodeDto
        {
            Id = d.Id,
            Name = d.Name,
            Code = d.Code,
            HeadName = d.HeadOfDepartment is not null
                ? $"{d.HeadOfDepartment.FirstName} {d.HeadOfDepartment.LastName}".Trim()
                : null,
            HeadPhotoUrl = d.HeadOfDepartment?.PhotoUrl,
            EmployeeCount = d.Employees.Count(e => !e.IsDeleted)
        }).ToList();

        return BuildOrgTree(nodes, departments);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static DepartmentDto MapToDto(Core.Entities.Core.Department d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Code = d.Code,
        Description = d.Description,
        ParentDepartmentId = d.ParentDepartmentId,
        ParentDepartmentName = d.ParentDepartment?.Name,
        CostCenter = d.CostCenter,
        HeadOfDepartmentId = d.HeadOfDepartmentId,
        HeadOfDepartmentName = d.HeadOfDepartment is not null
            ? $"{d.HeadOfDepartment.FirstName} {d.HeadOfDepartment.LastName}".Trim()
            : null,
        EmployeeCount = d.Employees.Count(e => !e.IsDeleted),
        IsActive = d.IsActive
    };

    private static List<OrgChartNodeDto> BuildOrgTree(
        List<OrgChartNodeDto> nodes,
        List<Core.Entities.Core.Department> departments)
    {
        var nodeLookup = nodes.ToDictionary(n => n.Id);
        var roots = new List<OrgChartNodeDto>();

        foreach (var dept in departments)
        {
            if (!nodeLookup.TryGetValue(dept.Id, out var node)) continue;

            if (dept.ParentDepartmentId.HasValue &&
                nodeLookup.TryGetValue(dept.ParentDepartmentId.Value, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        return roots;
    }
}
