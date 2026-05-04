using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Core;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Announcement;

public interface IAnnouncementService
{
    Task<Core.Entities.Core.Announcement> CreateAsync(string title, string content, int? departmentId, int authorId, DateTime? expiresAt = null);
    Task<List<Core.Entities.Core.Announcement>> GetActiveAsync(int? departmentId = null);
    Task<List<Core.Entities.Core.Announcement>> GetAllAsync();
    Task<Core.Entities.Core.Announcement> UpdateAsync(int id, string title, string content, int? departmentId, DateTime? expiresAt);
    Task DeleteAsync(int id);
}

public class AnnouncementService : IAnnouncementService
{
    private readonly NickHRDbContext _db;

    public AnnouncementService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<Core.Entities.Core.Announcement> CreateAsync(
        string title, string content, int? departmentId, int authorId, DateTime? expiresAt = null)
    {
        var author = await _db.Employees.FindAsync(authorId)
            ?? throw new KeyNotFoundException($"Author employee {authorId} not found.");

        if (departmentId.HasValue)
        {
            var dept = await _db.Departments.FindAsync(departmentId.Value)
                ?? throw new KeyNotFoundException($"Department {departmentId} not found.");
        }

        var announcement = new Core.Entities.Core.Announcement
        {
            Title = title,
            Content = content,
            DepartmentId = departmentId,
            AuthorId = authorId,
            ExpiresAt = expiresAt,
            IsActive = true,
            PublishedAt = DateTime.UtcNow
        };

        _db.Announcements.Add(announcement);
        await _db.SaveChangesAsync();
        return announcement;
    }

    public async Task<List<Core.Entities.Core.Announcement>> GetActiveAsync(int? departmentId = null)
    {
        var now = DateTime.UtcNow;

        var query = _db.Announcements
            .Include(a => a.Author)
            .Include(a => a.Department)
            .Where(a => !a.IsDeleted
                && a.IsActive
                && (a.ExpiresAt == null || a.ExpiresAt > now));

        // Return company-wide announcements + department-specific (if departmentId provided)
        if (departmentId.HasValue)
        {
            query = query.Where(a => a.DepartmentId == null || a.DepartmentId == departmentId.Value);
        }

        return await query.OrderByDescending(a => a.PublishedAt).ToListAsync();
    }

    public async Task<List<Core.Entities.Core.Announcement>> GetAllAsync()
    {
        return await _db.Announcements
            .Include(a => a.Author)
            .Include(a => a.Department)
            .Where(a => !a.IsDeleted)
            .OrderByDescending(a => a.PublishedAt)
            .ToListAsync();
    }

    public async Task<Core.Entities.Core.Announcement> UpdateAsync(
        int id, string title, string content, int? departmentId, DateTime? expiresAt)
    {
        var announcement = await _db.Announcements.FindAsync(id)
            ?? throw new KeyNotFoundException($"Announcement {id} not found.");

        if (departmentId.HasValue)
        {
            var dept = await _db.Departments.FindAsync(departmentId.Value)
                ?? throw new KeyNotFoundException($"Department {departmentId} not found.");
        }

        announcement.Title = title;
        announcement.Content = content;
        announcement.DepartmentId = departmentId;
        announcement.ExpiresAt = expiresAt;

        await _db.SaveChangesAsync();
        return announcement;
    }

    public async Task DeleteAsync(int id)
    {
        var announcement = await _db.Announcements.FindAsync(id)
            ?? throw new KeyNotFoundException($"Announcement {id} not found.");

        announcement.IsDeleted = true;
        await _db.SaveChangesAsync();
    }
}
