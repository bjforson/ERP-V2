using Microsoft.EntityFrameworkCore;
using NickHR.Core.DTOs;
using NickHR.Core.DTOs.Department;
using NickHR.Core.DTOs.Employee;
using NickHR.Core.Entities.Core;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Employee;

public class EmployeeService : IEmployeeService
{
    private readonly IRepository<Core.Entities.Core.Employee> _employeeRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly NickHRDbContext _db;

    public EmployeeService(
        IRepository<Core.Entities.Core.Employee> employeeRepo,
        IUnitOfWork unitOfWork,
        NickHRDbContext db)
    {
        _employeeRepo = employeeRepo;
        _unitOfWork = unitOfWork;
        _db = db;
    }

    public async Task<EmployeeDetailDto?> GetByIdAsync(int id)
    {
        var employee = await _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .Include(e => e.Grade)
            .Include(e => e.Location)
            .Include(e => e.ReportingManager)
            .Include(e => e.EmergencyContacts)
            .Include(e => e.Dependents)
            .Include(e => e.EmployeeQualifications)
            .Include(e => e.EmployeeDocuments)
            .FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted);

        return employee is null ? null : MapToDetailDto(employee);
    }

    public async Task<PagedResult<EmployeeListDto>> GetListAsync(EmployeeSearchFilter filter)
    {
        // Read-only path: AsNoTracking saves the change-tracker overhead and the
        // identity-map allocations. Wave 2L performance fix.
        var query = _db.Employees
            .AsNoTracking()
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .Include(e => e.Grade)
            .Where(e => !e.IsDeleted)
            .AsQueryable();

        // Filtering
        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var term = filter.SearchTerm.ToLower();
            query = query.Where(e =>
                e.FirstName.ToLower().Contains(term) ||
                e.LastName.ToLower().Contains(term) ||
                e.EmployeeCode.ToLower().Contains(term) ||
                (e.WorkEmail != null && e.WorkEmail.ToLower().Contains(term)));
        }

        if (filter.DepartmentId.HasValue)
            query = query.Where(e => e.DepartmentId == filter.DepartmentId.Value);

        if (filter.GradeId.HasValue)
            query = query.Where(e => e.GradeId == filter.GradeId.Value);

        if (filter.LocationId.HasValue)
            query = query.Where(e => e.LocationId == filter.LocationId.Value);

        if (filter.EmploymentStatus.HasValue)
            query = query.Where(e => e.EmploymentStatus == filter.EmploymentStatus.Value);

        if (filter.EmploymentType.HasValue)
            query = query.Where(e => e.EmploymentType == filter.EmploymentType.Value);

        if (filter.HireFromDate.HasValue)
            query = query.Where(e => e.HireDate >= filter.HireFromDate.Value);

        if (filter.HireToDate.HasValue)
            query = query.Where(e => e.HireDate <= filter.HireToDate.Value);

        // Sorting
        query = (filter.SortBy?.ToLower(), filter.SortDirection?.ToLower()) switch
        {
            ("firstname", "desc")  => query.OrderByDescending(e => e.FirstName),
            ("firstname", _)       => query.OrderBy(e => e.FirstName),
            ("lastname", "desc")   => query.OrderByDescending(e => e.LastName),
            ("lastname", _)        => query.OrderBy(e => e.LastName),
            ("hiredate", "desc")   => query.OrderByDescending(e => e.HireDate),
            ("hiredate", _)        => query.OrderBy(e => e.HireDate),
            ("employeecode", "desc") => query.OrderByDescending(e => e.EmployeeCode),
            ("employeecode", _)    => query.OrderBy(e => e.EmployeeCode),
            _                      => query.OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
        };

        var totalCount = await query.CountAsync();

        var page = filter.Page < 1 ? 1 : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new EmployeeListDto
            {
                Id = e.Id,
                EmployeeCode = e.EmployeeCode,
                FullName = $"{e.FirstName} {(e.MiddleName != null ? e.MiddleName + " " : "")}{e.LastName}".Trim(),
                Email = e.WorkEmail,
                Department = e.Department != null ? e.Department.Name : null,
                Designation = e.Designation != null ? e.Designation.Title : null,
                Grade = e.Grade != null ? e.Grade.Name : null,
                EmploymentStatus = e.EmploymentStatus,
                HireDate = e.HireDate,
                PhotoUrl = e.PhotoUrl
            })
            .ToListAsync();

        return new PagedResult<EmployeeListDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<EmployeeDetailDto> CreateAsync(CreateEmployeeDto dto)
    {
        var codeExists = await _employeeRepo.ExistsAsync(e => e.EmployeeCode == dto.Email && !e.IsDeleted);

        var employeeCode = await GenerateEmployeeCodeAsync();

        var employee = new Core.Entities.Core.Employee
        {
            EmployeeCode = employeeCode,
            FirstName = dto.FirstName,
            MiddleName = dto.MiddleName,
            LastName = dto.LastName,
            DateOfBirth = dto.DateOfBirth,
            Gender = dto.Gender,
            MaritalStatus = dto.MaritalStatus,
            Nationality = dto.Nationality,
            GhanaCardNumber = dto.GhanaCardNumber,
            TIN = dto.TIN,
            SSNITNumber = dto.SSNITNumber,
            PrimaryPhone = dto.PrimaryPhone,
            SecondaryPhone = dto.SecondaryPhone,
            PersonalEmail = dto.PersonalEmail,
            WorkEmail = dto.Email,
            ResidentialAddress = dto.ResidentialAddress,
            PostalAddress = dto.PostalAddress,
            Hometown = dto.Hometown,
            Region = dto.Region,
            DepartmentId = dto.DepartmentId,
            DesignationId = dto.DesignationId,
            GradeId = dto.GradeId,
            LocationId = dto.LocationId,
            ReportingManagerId = dto.ReportingManagerId,
            EmploymentType = dto.EmploymentType,
            EmploymentStatus = Core.Enums.EmploymentStatus.Active,
            HireDate = dto.HireDate,
            ProbationEndDate = dto.ProbationEndDate,
            BasicSalary = dto.BasicSalary,
            BankName = dto.BankName,
            BankBranch = dto.BankBranch,
            BankAccountNumber = dto.BankAccountNumber,
            MobileMoneyNumber = dto.MobileMoneyNumber
        };

        await _employeeRepo.AddAsync(employee);
        await _unitOfWork.SaveChangesAsync();

        return await GetByIdAsync(employee.Id)
            ?? throw new InvalidOperationException("Failed to retrieve newly created employee.");
    }

    public async Task<EmployeeDetailDto> UpdateAsync(int id, UpdateEmployeeDto dto)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted)
            ?? throw new KeyNotFoundException($"Employee with ID {id} not found.");

        // Apply only non-null fields (PATCH semantics)
        if (dto.FirstName is not null) employee.FirstName = dto.FirstName;
        if (dto.MiddleName is not null) employee.MiddleName = dto.MiddleName;
        if (dto.LastName is not null) employee.LastName = dto.LastName;
        if (dto.DateOfBirth.HasValue) employee.DateOfBirth = dto.DateOfBirth;
        if (dto.Gender.HasValue) employee.Gender = dto.Gender.Value;
        if (dto.MaritalStatus.HasValue) employee.MaritalStatus = dto.MaritalStatus.Value;
        if (dto.Nationality is not null) employee.Nationality = dto.Nationality;
        if (dto.GhanaCardNumber is not null) employee.GhanaCardNumber = dto.GhanaCardNumber;
        if (dto.TIN is not null) employee.TIN = dto.TIN;
        if (dto.SSNITNumber is not null) employee.SSNITNumber = dto.SSNITNumber;
        if (dto.PhotoUrl is not null) employee.PhotoUrl = dto.PhotoUrl;
        if (dto.PrimaryPhone is not null) employee.PrimaryPhone = dto.PrimaryPhone;
        if (dto.SecondaryPhone is not null) employee.SecondaryPhone = dto.SecondaryPhone;
        if (dto.PersonalEmail is not null) employee.PersonalEmail = dto.PersonalEmail;
        if (dto.WorkEmail is not null) employee.WorkEmail = dto.WorkEmail;
        if (dto.ResidentialAddress is not null) employee.ResidentialAddress = dto.ResidentialAddress;
        if (dto.PostalAddress is not null) employee.PostalAddress = dto.PostalAddress;
        if (dto.Hometown is not null) employee.Hometown = dto.Hometown;
        if (dto.Region is not null) employee.Region = dto.Region;
        if (dto.DepartmentId.HasValue) employee.DepartmentId = dto.DepartmentId;
        if (dto.DesignationId.HasValue) employee.DesignationId = dto.DesignationId;
        if (dto.GradeId.HasValue) employee.GradeId = dto.GradeId;
        if (dto.LocationId.HasValue) employee.LocationId = dto.LocationId;
        if (dto.ReportingManagerId.HasValue) employee.ReportingManagerId = dto.ReportingManagerId;
        if (dto.EmploymentType.HasValue) employee.EmploymentType = dto.EmploymentType.Value;
        if (dto.EmploymentStatus.HasValue) employee.EmploymentStatus = dto.EmploymentStatus.Value;
        if (dto.HireDate.HasValue) employee.HireDate = dto.HireDate.Value;
        if (dto.ConfirmationDate.HasValue) employee.ConfirmationDate = dto.ConfirmationDate;
        if (dto.ProbationEndDate.HasValue) employee.ProbationEndDate = dto.ProbationEndDate;
        if (dto.BasicSalary.HasValue) employee.BasicSalary = dto.BasicSalary.Value;
        if (dto.BankName is not null) employee.BankName = dto.BankName;
        if (dto.BankBranch is not null) employee.BankBranch = dto.BankBranch;
        if (dto.BankAccountNumber is not null) employee.BankAccountNumber = dto.BankAccountNumber;
        if (dto.MobileMoneyNumber is not null) employee.MobileMoneyNumber = dto.MobileMoneyNumber;

        _employeeRepo.Update(employee);
        await _unitOfWork.SaveChangesAsync();

        return await GetByIdAsync(employee.Id)
            ?? throw new InvalidOperationException("Failed to retrieve updated employee.");
    }

    public async Task DeleteAsync(int id)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted)
            ?? throw new KeyNotFoundException($"Employee with ID {id} not found.");

        _employeeRepo.SoftDelete(employee);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<EmployeeDetailDto?> GetByCodeAsync(string employeeCode)
    {
        var employee = await _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .Include(e => e.Grade)
            .Include(e => e.Location)
            .Include(e => e.ReportingManager)
            .Include(e => e.EmergencyContacts)
            .Include(e => e.Dependents)
            .Include(e => e.EmployeeQualifications)
            .Include(e => e.EmployeeDocuments)
            .FirstOrDefaultAsync(e => e.EmployeeCode == employeeCode && !e.IsDeleted);

        return employee is null ? null : MapToDetailDto(employee);
    }

    public async Task<List<OrgChartNodeDto>> GetOrgChartAsync()
    {
        // Delegate to DepartmentService logic; return flat employee hierarchy rooted by reporting manager
        var employees = await _db.Employees
            .Include(e => e.Department)
            .Where(e => !e.IsDeleted)
            .ToListAsync();

        // Build department-based org chart nodes
        var departments = await _db.Departments
            .Include(d => d.HeadOfDepartment)
            .Where(d => !d.IsDeleted && d.IsActive)
            .ToListAsync();

        var employeeCountByDept = employees
            .Where(e => e.DepartmentId.HasValue)
            .GroupBy(e => e.DepartmentId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var all = departments.Select(d => new OrgChartNodeDto
        {
            Id = d.Id,
            Name = d.Name,
            Code = d.Code,
            HeadName = d.HeadOfDepartment is not null
                ? $"{d.HeadOfDepartment.FirstName} {d.HeadOfDepartment.LastName}".Trim()
                : null,
            HeadPhotoUrl = d.HeadOfDepartment?.PhotoUrl,
            EmployeeCount = employeeCountByDept.GetValueOrDefault(d.Id, 0)
        }).ToList();

        return BuildOrgTree(all, departments);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<string> GenerateEmployeeCodeAsync()
    {
        var last = await _db.Employees
            .OrderByDescending(e => e.Id)
            .Select(e => e.EmployeeCode)
            .FirstOrDefaultAsync();

        int next = 1;
        if (last is not null && last.StartsWith("NHR-") &&
            int.TryParse(last[4..], out var num))
        {
            next = num + 1;
        }

        return $"NHR-{next:D4}";
    }

    private static List<OrgChartNodeDto> BuildOrgTree(
        List<OrgChartNodeDto> nodes,
        List<Core.Entities.Core.Department> departments)
    {
        var lookup = nodes.ToDictionary(n => n.Id);

        var roots = new List<OrgChartNodeDto>();
        foreach (var dept in departments)
        {
            if (!lookup.TryGetValue(dept.Id, out var node)) continue;

            if (dept.ParentDepartmentId.HasValue &&
                lookup.TryGetValue(dept.ParentDepartmentId.Value, out var parent))
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

    private static EmployeeDetailDto MapToDetailDto(Core.Entities.Core.Employee e) => new()
    {
        Id = e.Id,
        EmployeeCode = e.EmployeeCode,
        FirstName = e.FirstName,
        MiddleName = e.MiddleName,
        LastName = e.LastName,
        DateOfBirth = e.DateOfBirth,
        Gender = e.Gender,
        MaritalStatus = e.MaritalStatus,
        Nationality = e.Nationality,
        GhanaCardNumber = e.GhanaCardNumber,
        TIN = e.TIN,
        SSNITNumber = e.SSNITNumber,
        PhotoUrl = e.PhotoUrl,
        DigitalSignatureUrl = e.DigitalSignatureUrl,
        PrimaryPhone = e.PrimaryPhone,
        SecondaryPhone = e.SecondaryPhone,
        PersonalEmail = e.PersonalEmail,
        WorkEmail = e.WorkEmail,
        ResidentialAddress = e.ResidentialAddress,
        PostalAddress = e.PostalAddress,
        Hometown = e.Hometown,
        Region = e.Region,
        HireDate = e.HireDate,
        ConfirmationDate = e.ConfirmationDate,
        EmploymentType = e.EmploymentType,
        EmploymentStatus = e.EmploymentStatus,
        ProbationEndDate = e.ProbationEndDate,
        DepartmentId = e.DepartmentId,
        Department = e.Department?.Name,
        DesignationId = e.DesignationId,
        Designation = e.Designation?.Title,
        GradeId = e.GradeId,
        Grade = e.Grade?.Name,
        LocationId = e.LocationId,
        Location = e.Location?.Name,
        ReportingManagerId = e.ReportingManagerId,
        ReportingManagerName = e.ReportingManager is not null
            ? $"{e.ReportingManager.FirstName} {e.ReportingManager.LastName}".Trim()
            : null,
        BasicSalary = e.BasicSalary,
        BankName = e.BankName,
        BankBranch = e.BankBranch,
        BankAccountNumber = e.BankAccountNumber,
        MobileMoneyNumber = e.MobileMoneyNumber,
        EmergencyContacts = e.EmergencyContacts.Select(ec => new EmergencyContactDto
        {
            Id = ec.Id,
            FullName = ec.FullName,
            Relationship = ec.Relationship,
            PrimaryPhone = ec.PrimaryPhone,
            SecondaryPhone = ec.SecondaryPhone,
            Email = ec.Email,
            Address = ec.Address
        }).ToList(),
        Dependents = e.Dependents.Select(d => new DependentDto
        {
            Id = d.Id,
            FullName = d.FullName,
            Relationship = d.Relationship,
            DateOfBirth = d.DateOfBirth,
            Gender = d.Gender,
            IsActive = d.IsActive
        }).ToList(),
        Qualifications = e.EmployeeQualifications.Select(q => new EmployeeQualificationDto
        {
            Id = q.Id,
            QualificationType = q.QualificationType,
            Institution = q.Institution,
            Qualification = q.Qualification,
            FieldOfStudy = q.FieldOfStudy,
            StartDate = q.StartDate,
            EndDate = q.EndDate,
            Grade = q.Grade,
            IsHighest = q.IsHighest
        }).ToList(),
        Documents = e.EmployeeDocuments.Select(doc => new EmployeeDocumentDto
        {
            Id = doc.Id,
            DocumentType = doc.DocumentType,
            FileName = doc.FileName,
            FilePath = doc.FilePath,
            FileSize = doc.FileSize,
            ContentType = doc.ContentType,
            Description = doc.Description,
            UploadedAt = doc.UploadedAt,
            Version = doc.Version
        }).ToList()
    };
}
