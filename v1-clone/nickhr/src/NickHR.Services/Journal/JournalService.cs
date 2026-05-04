using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Core;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Journal;

public class JournalService
{
    private readonly NickHRDbContext _db;

    public JournalService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<List<AchievementEntry>> GetMyEntriesAsync(int employeeId)
    {
        return await _db.Set<AchievementEntry>()
            .Where(a => a.EmployeeId == employeeId)
            .Include(a => a.LinkedGoal)
            .OrderByDescending(a => a.EntryDate)
            .ToListAsync();
    }

    public async Task<AchievementEntry> CreateEntryAsync(AchievementEntry entry)
    {
        _db.Set<AchievementEntry>().Add(entry);
        await _db.SaveChangesAsync();
        return entry;
    }

    public async Task<AchievementEntry> UpdateEntryAsync(AchievementEntry entry)
    {
        _db.Set<AchievementEntry>().Update(entry);
        await _db.SaveChangesAsync();
        return entry;
    }

    public async Task DeleteEntryAsync(int id, int employeeId)
    {
        var entry = await _db.Set<AchievementEntry>()
            .FirstOrDefaultAsync(a => a.Id == id && a.EmployeeId == employeeId)
            ?? throw new KeyNotFoundException("Entry not found.");

        entry.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<List<AchievementEntry>> GetEntriesByGoalAsync(int goalId)
    {
        return await _db.Set<AchievementEntry>()
            .Where(a => a.LinkedGoalId == goalId)
            .OrderByDescending(a => a.EntryDate)
            .ToListAsync();
    }
}
