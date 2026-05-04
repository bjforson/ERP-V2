using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NickHR.Core.DTOs;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.SeniorHR)]
public class ImportController : ControllerBase
{
    private readonly NickHRDbContext _db;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _currentUser;

    public ImportController(
        NickHRDbContext db,
        IAuditService audit,
        ICurrentUserService currentUser)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
    }

    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>Preview import - parse Excel and show what will be imported without saving.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> PreviewImport(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse<object>.Fail("No file uploaded."));

        var results = await ParseExcel(file);
        return Ok(ApiResponse<object>.Ok(new
        {
            TotalRows = results.Count,
            Valid = results.Count(r => r.IsValid),
            Invalid = results.Count(r => !r.IsValid),
            Rows = results
        }, "Preview generated. Review and confirm to import."));
    }

    /// <summary>Execute import - parse Excel and save employees to database.</summary>
    /// <remarks>
    /// Wave 2I: each batch of imports is wrapped in an outer transaction so a
    /// crash midway can't leave a half-applied import. Per-row failures are
    /// collected into <c>errors</c> and the row is rolled back via SAVEPOINT
    /// (so other rows still commit). If we hit a *systemic* failure (e.g. lost
    /// DB connection) the outer transaction rolls back the entire batch.
    /// Wave 2G: audit-log entry on each invocation captures who triggered the
    /// import and the file's metadata.
    /// </remarks>
    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteImport(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse<object>.Fail("No file uploaded."));

        var results = await ParseExcel(file);
        var validRows = results.Where(r => r.IsValid).ToList();

        if (!validRows.Any())
            return BadRequest(ApiResponse<object>.Fail("No valid rows to import."));

        int imported = 0, skipped = 0;
        var errors = new List<string>();

        // Outer transaction — committed only if the foreach completes without
        // an outer-level exception. Per-row failures use a SAVEPOINT so one bad
        // row doesn't abort the whole import.
        await using var tx = await _db.Database.BeginTransactionAsync();

        try
        {
            foreach (var row in validRows)
            {
                var savepointName = $"row_{row.RowNumber}";
                await tx.CreateSavepointAsync(savepointName);

                try
                {
                    // Check for duplicate by EmployeeCode or Email
                    if (!string.IsNullOrEmpty(row.EmployeeCode))
                    {
                        var exists = await _db.Employees.AnyAsync(e => e.EmployeeCode == row.EmployeeCode && !e.IsDeleted);
                        if (exists)
                        {
                            skipped++;
                            row.ImportNote = "Skipped: duplicate EmployeeCode";
                            await tx.ReleaseSavepointAsync(savepointName);
                            continue;
                        }
                    }

                    // Resolve Department (from Unit field)
                    var department = await ResolveOrCreateDepartment(row.Unit);
                    // Resolve Designation
                    var designation = await ResolveOrCreateDesignation(row.JobPosition);
                    // Resolve Location
                    var location = await ResolveLocation(row.Location);
                    // Resolve Grade by salary
                    var grade = await ResolveGradeBySalary(row.BasicSalary);

                    var employee = new Employee
                    {
                        FirstName = row.FirstName!.Trim(),
                        LastName = row.LastName!.Trim(),
                        EmployeeCode = row.EmployeeCode ?? await GenerateEmployeeCode(),
                        BasicSalary = row.BasicSalary,
                        SSNITNumber = NormalizeString(row.SSNITNumber),
                        WorkEmail = NormalizeString(row.Email),
                        PrimaryPhone = NormalizeString(row.Phone),
                        Gender = ParseGender(row.Gender),
                        MaritalStatus = ParseMaritalStatus(row.MaritalStatus),
                        Nationality = row.Nationality ?? "Ghana",
                        GhanaCardNumber = NormalizeString(row.NationalIdNumber),
                        DepartmentId = department.Id,
                        DesignationId = designation.Id,
                        LocationId = location?.Id ?? 1,
                        GradeId = grade.Id,
                        EmploymentType = EmploymentType.FullTime,
                        EmploymentStatus = EmploymentStatus.Active,
                        HireDate = ParseDate(row.HireDate) ?? DateTime.UtcNow,
                    };

                    _db.Employees.Add(employee);
                    await _db.SaveChangesAsync();
                    imported++;
                    row.ImportNote = $"Imported as {employee.EmployeeCode}";
                    await tx.ReleaseSavepointAsync(savepointName);
                }
                catch (Exception ex)
                {
                    // Single-row failure: roll back to the savepoint so the rest
                    // of the batch survives. Detach the failed entity so it
                    // doesn't get re-saved on the next iteration.
                    await tx.RollbackToSavepointAsync(savepointName);
                    foreach (var e in _db.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged).ToList())
                    {
                        e.State = EntityState.Detached;
                    }

                    errors.Add($"Row {row.RowNumber}: {ex.Message}");
                    row.ImportNote = $"Error: {ex.Message}";
                    skipped++;
                }
            }

            await tx.CommitAsync();
        }
        catch
        {
            // Outer-level failure (e.g. connection drop) — abandon everything.
            await tx.RollbackAsync();
            throw;
        }

        await _audit.LogAsync(
            userId: _currentUser.UserId,
            action: "Import.ExecuteEmployees",
            entityType: "EmployeeImport",
            entityId: file.FileName ?? "<inline>",
            oldValues: null,
            newValues: System.Text.Json.JsonSerializer.Serialize(new { imported, skipped, errorCount = errors.Count }),
            ipAddress: RemoteIp);

        return Ok(ApiResponse<object>.Ok(new
        {
            Imported = imported,
            Skipped = skipped,
            TotalRows = results.Count,
            InvalidRows = results.Count(r => !r.IsValid),
            Errors = errors,
            Results = results
        }, $"Import complete: {imported} imported, {skipped} skipped."));
    }

    /// <summary>Import from the pre-saved staff file on server.</summary>
    [HttpPost("execute-server-file")]
    public async Task<IActionResult> ExecuteServerFileImport()
    {
        var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "data", "import", "staff_payroll_data.xlsx");

        // Try multiple possible paths
        var possiblePaths = new[]
        {
            filePath,
            @"C:\Shared\NSCIM_PRODUCTION\NickHR\data\import\staff_payroll_data.xlsx"
        };

        var actualPath = possiblePaths.FirstOrDefault(System.IO.File.Exists);
        if (actualPath == null)
            return BadRequest(ApiResponse<object>.Fail("Staff data file not found on server."));

        using var stream = System.IO.File.OpenRead(actualPath);
        var formFile = new FormFile(stream, 0, stream.Length, "file", Path.GetFileName(actualPath));
        return await ExecuteImport(formFile);
    }

    private async Task<List<ImportRow>> ParseExcel(IFormFile file)
    {
        var results = new List<ImportRow>();
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        stream.Position = 0;

        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var headerRow = 2; // Row 2 has headers based on analysis
        var dataStartRow = 3;

        // Map column indices
        var headers = new Dictionary<string, int>();
        for (int col = 1; col <= (sheet.LastColumnUsed()?.ColumnNumber() ?? 0); col++)
        {
            var header = sheet.Cell(headerRow, col).GetString()?.Trim();
            if (!string.IsNullOrEmpty(header))
                headers[header] = col;
        }

        for (int row = dataStartRow; row <= sheet.LastRowUsed()?.RowNumber(); row++)
        {
            var firstName = GetCell(sheet, row, headers, "FirstName");
            var lastName = GetCell(sheet, row, headers, "LastName");

            if (string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(lastName))
                continue;

            var importRow = new ImportRow
            {
                RowNumber = row,
                FirstName = firstName,
                LastName = lastName,
                BasicSalary = ParseDecimal(GetCell(sheet, row, headers, "BasicSalary")),
                SSNITNumber = GetCell(sheet, row, headers, "SocialSecurityNumber"),
                Email = GetCell(sheet, row, headers, "EmailAddress"),
                Phone = GetCell(sheet, row, headers, "MobileNumber"),
                Gender = GetCell(sheet, row, headers, "Gender"),
                MaritalStatus = GetCell(sheet, row, headers, "MaritalStatus"),
                Nationality = GetCell(sheet, row, headers, "Nationality"),
                NationalIdNumber = GetCell(sheet, row, headers, "NationalIdNumber"),
                EmployeeCode = GetCell(sheet, row, headers, "EmployeeNumber"),
                HireDate = GetCell(sheet, row, headers, "HireDate"),
                JobPosition = NormalizeJobTitle(GetCell(sheet, row, headers, "JobPosition")),
                Unit = NormalizeLocation(GetCell(sheet, row, headers, "Unit")),
                Location = NormalizeLocation(GetCell(sheet, row, headers, "Location")),
            };

            // Validate
            if (string.IsNullOrWhiteSpace(importRow.FirstName))
                importRow.Errors.Add("FirstName is required");
            if (string.IsNullOrWhiteSpace(importRow.LastName))
                importRow.Errors.Add("LastName is required");
            if (importRow.BasicSalary <= 0)
                importRow.Errors.Add("BasicSalary must be positive");

            results.Add(importRow);
        }

        return results;
    }

    private string? GetCell(IXLWorksheet sheet, int row, Dictionary<string, int> headers, string column)
    {
        if (!headers.ContainsKey(column)) return null;
        var val = sheet.Cell(row, headers[column]).GetString()?.Trim();
        return string.IsNullOrWhiteSpace(val) ? null : val;
    }

    private static string? NormalizeLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().ToUpper();
        return value switch
        {
            "TEMA-MAIN" or " TEMA MAIN" => "TEMA MAIN",
            "TEMA-SENTRY PORTAL" => "TEMA SENTRY PORTAL",
            "TEMA - SENTRY PORTAL" => "TEMA SENTRY PORTAL",
            "HEAD OFFICE" or "Head Office" => "HEAD OFFICE",
            _ => value
        };
    }

    private static string? NormalizeJobTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value switch
        {
            "COODINATOR" => "COORDINATOR",
            " DRIVER" => "DRIVER",
            _ => value
        };
    }

    private async Task<Department> ResolveOrCreateDepartment(string? name)
    {
        name = name?.Trim() ?? "UNASSIGNED";
        var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Name == name && !d.IsDeleted);
        if (dept != null) return dept;

        dept = new Department
        {
            Name = name,
            Code = name.Replace(" ", "-").ToUpper()[..Math.Min(name.Length, 10)],
            IsActive = true
        };
        _db.Departments.Add(dept);
        await _db.SaveChangesAsync();
        return dept;
    }

    private async Task<Designation> ResolveOrCreateDesignation(string? title)
    {
        title = title?.Trim() ?? "UNASSIGNED";
        var desig = await _db.Designations.FirstOrDefaultAsync(d => d.Title == title && !d.IsDeleted);
        if (desig != null) return desig;

        desig = new Designation
        {
            Title = title,
            Code = title.Replace(" ", "-").ToUpper()[..Math.Min(title.Length, 10)],
            IsActive = true
        };
        _db.Designations.Add(desig);
        await _db.SaveChangesAsync();
        return desig;
    }

    private async Task<Location?> ResolveLocation(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();

        // Try exact match
        var loc = await _db.Locations.FirstOrDefaultAsync(l => l.Name.ToUpper().Contains(name.ToUpper()) && !l.IsDeleted);
        if (loc != null) return loc;

        // Try partial match on city
        loc = await _db.Locations.FirstOrDefaultAsync(l => l.City != null && l.City.ToUpper().Contains(name.ToUpper()) && !l.IsDeleted);
        return loc;
    }

    private async Task<Grade> ResolveGradeBySalary(decimal salary)
    {
        var grade = await _db.Grades
            .Where(g => g.IsActive && !g.IsDeleted && salary >= g.MinSalary && salary <= g.MaxSalary)
            .FirstOrDefaultAsync();

        if (grade != null) return grade;

        // Fallback: closest grade by mid salary
        grade = await _db.Grades
            .Where(g => g.IsActive && !g.IsDeleted)
            .OrderBy(g => Math.Abs(g.MidSalary - salary))
            .FirstOrDefaultAsync();

        return grade ?? (await _db.Grades.FirstAsync(g => !g.IsDeleted));
    }

    private async Task<string> GenerateEmployeeCode()
    {
        var last = await _db.Employees
            .Where(e => e.EmployeeCode.StartsWith("NHR-"))
            .OrderByDescending(e => e.EmployeeCode)
            .Select(e => e.EmployeeCode)
            .FirstOrDefaultAsync();

        int next = 1;
        if (last != null && int.TryParse(last.Replace("NHR-", ""), out var num))
            next = num + 1;

        return $"NHR-{next:D4}";
    }

    private static Gender ParseGender(string? value) => value?.Trim().ToUpper() switch
    {
        "MALE" => Gender.Male,
        "FEMALE" => Gender.Female,
        _ => Gender.Other
    };

    private static MaritalStatus ParseMaritalStatus(string? value) => value?.Trim().ToUpper() switch
    {
        "MARRIED" => MaritalStatus.Married,
        "SINGLE" => MaritalStatus.Single,
        "DIVORCED" => MaritalStatus.Divorced,
        "WIDOWED" => MaritalStatus.Widowed,
        _ => MaritalStatus.Single
    };

    private static decimal ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        return decimal.TryParse(value.Replace(",", ""), out var d) ? d : 0;
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Try DD/MM/YYYY format (Ghana format)
        if (DateTime.TryParseExact(value, new[] { "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "yyyy-MM-dd" },
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        if (DateTime.TryParse(value, out dt))
            return dt;
        return null;
    }

    private static string? NormalizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }
}

public class ImportRow
{
    public int RowNumber { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public decimal BasicSalary { get; set; }
    public string? SSNITNumber { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Gender { get; set; }
    public string? MaritalStatus { get; set; }
    public string? Nationality { get; set; }
    public string? NationalIdNumber { get; set; }
    public string? EmployeeCode { get; set; }
    public string? HireDate { get; set; }
    public string? JobPosition { get; set; }
    public string? Unit { get; set; }
    public string? Location { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool IsValid => !Errors.Any();
    public string? ImportNote { get; set; }
}
