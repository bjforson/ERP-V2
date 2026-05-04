using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Training;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Training;

public interface ITrainingService
{
    Task<TrainingProgram> CreateProgramAsync(string title, string? description, string? provider,
        string? location, DateTime? startDate, DateTime? endDate, int maxParticipants,
        decimal cost, string trainingType, int? departmentId);
    Task<List<TrainingProgram>> GetProgramsAsync(bool? activeOnly = null);
    Task<TrainingProgram?> GetProgramByIdAsync(int id);
    Task<TrainingAttendance> EnrollEmployeeAsync(int programId, int employeeId);
    Task<TrainingAttendance> UpdateAttendanceAsync(int attendanceId, string status,
        string? certName, DateTime? certExpiry, decimal? score);
    Task<List<TrainingAttendance>> GetEmployeeTrainingHistoryAsync(int employeeId);
    Task<List<Skill>> GetSkillsAsync();
    Task<EmployeeSkill> AddEmployeeSkillAsync(int employeeId, int skillId, int proficiencyLevel);
    Task<List<EmployeeSkill>> GetEmployeeSkillsAsync(int employeeId);
    Task<object> GetSkillGapReportAsync(int? departmentId = null);
}

public class TrainingService : ITrainingService
{
    private readonly NickHRDbContext _db;

    public TrainingService(NickHRDbContext db)
    {
        _db = db;
    }

    // ─── Programs ────────────────────────────────────────────────────────────

    public async Task<TrainingProgram> CreateProgramAsync(string title, string? description, string? provider,
        string? location, DateTime? startDate, DateTime? endDate, int maxParticipants,
        decimal cost, string trainingType, int? departmentId)
    {
        var program = new TrainingProgram
        {
            Title = title,
            Description = description,
            Provider = provider,
            Location = location,
            StartDate = startDate,
            EndDate = endDate,
            MaxParticipants = maxParticipants,
            Cost = cost,
            TrainingType = trainingType,
            DepartmentId = departmentId,
            IsActive = true
        };

        _db.TrainingPrograms.Add(program);
        await _db.SaveChangesAsync();
        return program;
    }

    public async Task<List<TrainingProgram>> GetProgramsAsync(bool? activeOnly = null)
    {
        var query = _db.TrainingPrograms
            .Where(p => !p.IsDeleted)
            .Include(p => p.Department)
            .AsQueryable();

        if (activeOnly.HasValue)
            query = query.Where(p => p.IsActive == activeOnly.Value);

        return await query.OrderByDescending(p => p.StartDate).ToListAsync();
    }

    public async Task<TrainingProgram?> GetProgramByIdAsync(int id)
    {
        return await _db.TrainingPrograms
            .Where(p => p.Id == id && !p.IsDeleted)
            .Include(p => p.Department)
            .Include(p => p.TrainingAttendances)
                .ThenInclude(a => a.Employee)
            .FirstOrDefaultAsync();
    }

    // ─── Attendance / Enrollment ─────────────────────────────────────────────

    public async Task<TrainingAttendance> EnrollEmployeeAsync(int programId, int employeeId)
    {
        var program = await _db.TrainingPrograms.FindAsync(programId)
            ?? throw new KeyNotFoundException($"Training program {programId} not found.");

        var existing = await _db.TrainingAttendances
            .FirstOrDefaultAsync(a => a.TrainingProgramId == programId
                && a.EmployeeId == employeeId && !a.IsDeleted);

        if (existing is not null)
            throw new InvalidOperationException("Employee is already enrolled in this program.");

        var currentCount = await _db.TrainingAttendances
            .CountAsync(a => a.TrainingProgramId == programId && !a.IsDeleted);

        if (currentCount >= program.MaxParticipants)
            throw new InvalidOperationException("Training program is at full capacity.");

        var attendance = new TrainingAttendance
        {
            TrainingProgramId = programId,
            EmployeeId = employeeId,
            Status = "Enrolled"
        };

        _db.TrainingAttendances.Add(attendance);
        await _db.SaveChangesAsync();
        return attendance;
    }

    public async Task<TrainingAttendance> UpdateAttendanceAsync(int attendanceId, string status,
        string? certName, DateTime? certExpiry, decimal? score)
    {
        var attendance = await _db.TrainingAttendances.FindAsync(attendanceId)
            ?? throw new KeyNotFoundException($"Training attendance record {attendanceId} not found.");

        attendance.Status = status;
        attendance.CertificationName = certName;
        attendance.CertificationExpiryDate = certExpiry;
        attendance.Score = score;
        attendance.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return attendance;
    }

    public async Task<List<TrainingAttendance>> GetEmployeeTrainingHistoryAsync(int employeeId)
    {
        return await _db.TrainingAttendances
            .Where(a => a.EmployeeId == employeeId && !a.IsDeleted)
            .Include(a => a.TrainingProgram)
            .OrderByDescending(a => a.TrainingProgram.StartDate)
            .ToListAsync();
    }

    // ─── Skills ──────────────────────────────────────────────────────────────

    public async Task<List<Skill>> GetSkillsAsync()
    {
        return await _db.Skills
            .Where(s => !s.IsDeleted && s.IsActive)
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<EmployeeSkill> AddEmployeeSkillAsync(int employeeId, int skillId, int proficiencyLevel)
    {
        var existing = await _db.EmployeeSkills
            .FirstOrDefaultAsync(es => es.EmployeeId == employeeId && es.SkillId == skillId && !es.IsDeleted);

        if (existing is not null)
        {
            existing.ProficiencyLevel = proficiencyLevel;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return existing;
        }

        var employeeSkill = new EmployeeSkill
        {
            EmployeeId = employeeId,
            SkillId = skillId,
            ProficiencyLevel = proficiencyLevel
        };

        _db.EmployeeSkills.Add(employeeSkill);
        await _db.SaveChangesAsync();
        return employeeSkill;
    }

    public async Task<List<EmployeeSkill>> GetEmployeeSkillsAsync(int employeeId)
    {
        return await _db.EmployeeSkills
            .Where(es => es.EmployeeId == employeeId && !es.IsDeleted)
            .Include(es => es.Skill)
            .OrderBy(es => es.Skill.Category)
            .ThenBy(es => es.Skill.Name)
            .ToListAsync();
    }

    public async Task<object> GetSkillGapReportAsync(int? departmentId = null)
    {
        var employeesQuery = _db.Employees
            .Where(e => !e.IsDeleted)
            .AsQueryable();

        if (departmentId.HasValue)
            employeesQuery = employeesQuery.Where(e => e.DepartmentId == departmentId.Value);

        var employeeIds = await employeesQuery.Select(e => e.Id).ToListAsync();

        var skillData = await _db.EmployeeSkills
            .Where(es => !es.IsDeleted && employeeIds.Contains(es.EmployeeId))
            .Include(es => es.Skill)
            .GroupBy(es => new { es.SkillId, es.Skill.Name, es.Skill.Category })
            .Select(g => new
            {
                SkillId = g.Key.SkillId,
                SkillName = g.Key.Name,
                Category = g.Key.Category,
                EmployeeCount = g.Count(),
                AverageProficiency = g.Average(x => (double)x.ProficiencyLevel),
                ExpertCount = g.Count(x => x.ProficiencyLevel >= 4)
            })
            .OrderBy(s => s.Category)
            .ThenBy(s => s.SkillName)
            .ToListAsync();

        return new
        {
            DepartmentId = departmentId,
            TotalEmployees = employeeIds.Count,
            SkillCoverage = skillData
        };
    }
}
