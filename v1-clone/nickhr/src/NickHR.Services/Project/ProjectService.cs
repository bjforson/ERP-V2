using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Project;

public class ProjectService
{
    private readonly NickHRDbContext _db;

    public ProjectService(NickHRDbContext db)
    {
        _db = db;
    }

    // Projects
    public async Task<List<Core.Entities.Core.Project>> GetAllProjectsAsync()
    {
        return await _db.Set<Core.Entities.Core.Project>()
            .Include(p => p.Manager)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    public async Task<Core.Entities.Core.Project?> GetProjectByIdAsync(int id)
    {
        return await _db.Set<Core.Entities.Core.Project>()
            .Include(p => p.Manager)
            .Include(p => p.TimesheetEntries)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<Core.Entities.Core.Project> CreateProjectAsync(Core.Entities.Core.Project project)
    {
        _db.Set<Core.Entities.Core.Project>().Add(project);
        await _db.SaveChangesAsync();
        return project;
    }

    public async Task<Core.Entities.Core.Project> UpdateProjectAsync(Core.Entities.Core.Project project)
    {
        _db.Set<Core.Entities.Core.Project>().Update(project);
        await _db.SaveChangesAsync();
        return project;
    }

    // Timesheets
    public async Task<List<TimesheetEntry>> GetTimesheetsAsync(int employeeId, DateTime weekStart)
    {
        var weekEnd = weekStart.AddDays(7);
        return await _db.Set<TimesheetEntry>()
            .Where(t => t.EmployeeId == employeeId && t.Date >= weekStart && t.Date < weekEnd)
            .Include(t => t.Project)
            .OrderBy(t => t.Date)
            .ToListAsync();
    }

    public async Task<TimesheetEntry> CreateTimesheetEntryAsync(TimesheetEntry entry)
    {
        _db.Set<TimesheetEntry>().Add(entry);
        await _db.SaveChangesAsync();
        return entry;
    }

    public async Task<TimesheetEntry> UpdateTimesheetEntryAsync(TimesheetEntry entry)
    {
        _db.Set<TimesheetEntry>().Update(entry);
        await _db.SaveChangesAsync();
        return entry;
    }

    public async Task SubmitWeekAsync(int employeeId, DateTime weekStart)
    {
        var weekEnd = weekStart.AddDays(7);
        var draftStatus = nameof(TimesheetStatus.Draft);
        var entries = await _db.Set<TimesheetEntry>()
            .Where(t => t.EmployeeId == employeeId && t.Date >= weekStart && t.Date < weekEnd && t.Status == draftStatus)
            .ToListAsync();

        foreach (var entry in entries)
            entry.Status = nameof(TimesheetStatus.Submitted);

        await _db.SaveChangesAsync();
    }

    public async Task ApproveTimesheetAsync(int entryId)
    {
        var entry = await _db.Set<TimesheetEntry>().FindAsync(entryId)
            ?? throw new KeyNotFoundException("Timesheet entry not found.");

        entry.Status = nameof(TimesheetStatus.Approved);
        await _db.SaveChangesAsync();
    }
}
