using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Recognition;

public class RecognitionService
{
    private readonly NickHRDbContext _db;

    public RecognitionService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<NickHR.Core.Entities.Core.Recognition> SendKudosAsync(
        int senderId, int recipientId, string message, RecognitionCategory category, int points = 10)
    {
        var recognition = new NickHR.Core.Entities.Core.Recognition
        {
            SenderEmployeeId = senderId,
            RecipientEmployeeId = recipientId,
            Message = message,
            Category = category,
            Points = points,
            IsPublic = true
        };

        _db.Recognitions.Add(recognition);
        await _db.SaveChangesAsync();
        return recognition;
    }

    public async Task<List<object>> GetFeedAsync(int count = 20)
    {
        return await _db.Recognitions
            .Where(r => r.IsPublic)
            .OrderByDescending(r => r.CreatedAt)
            .Take(count)
            .Select(r => (object)new
            {
                r.Id,
                r.Message,
                r.Category,
                r.Points,
                r.CreatedAt,
                Sender = r.SenderEmployee.FirstName + " " + r.SenderEmployee.LastName,
                Recipient = r.RecipientEmployee.FirstName + " " + r.RecipientEmployee.LastName,
                RecipientPhoto = r.RecipientEmployee.PhotoUrl
            })
            .ToListAsync();
    }

    public async Task<List<object>> GetEmployeeRecognitionsAsync(int employeeId)
    {
        return await _db.Recognitions
            .Where(r => r.RecipientEmployeeId == employeeId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => (object)new
            {
                r.Id,
                r.Message,
                r.Category,
                r.Points,
                r.IsPublic,
                r.CreatedAt,
                Sender = r.SenderEmployee.FirstName + " " + r.SenderEmployee.LastName
            })
            .ToListAsync();
    }

    public async Task<List<object>> GetLeaderboardAsync(int month, int year)
    {
        var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = startDate.AddMonths(1);

        var grouped = await _db.Recognitions
            .Where(r => r.CreatedAt >= startDate && r.CreatedAt < endDate)
            .GroupBy(r => new
            {
                r.RecipientEmployeeId,
                FirstName = r.RecipientEmployee.FirstName,
                LastName = r.RecipientEmployee.LastName,
                Photo = r.RecipientEmployee.PhotoUrl
            })
            .Select(g => new
            {
                EmployeeId = g.Key.RecipientEmployeeId,
                FullName = g.Key.FirstName + " " + g.Key.LastName,
                PhotoUrl = g.Key.Photo,
                TotalPoints = g.Sum(r => r.Points),
                RecognitionCount = g.Count()
            })
            .OrderByDescending(x => x.TotalPoints)
            .Take(10)
            .ToListAsync();

        return grouped.Cast<object>().ToList();
    }

    public async Task<EmployeeOfMonth> NominateForEmployeeOfMonthAsync(
        int employeeId, int nominatedById, int month, int year)
    {
        var existing = await _db.EmployeesOfMonth
            .FirstOrDefaultAsync(e => e.EmployeeId == employeeId && e.Month == month && e.Year == year);

        if (existing != null)
            throw new InvalidOperationException("Employee is already nominated for this month.");

        var nomination = new EmployeeOfMonth
        {
            EmployeeId = employeeId,
            NominatedById = nominatedById,
            Month = month,
            Year = year,
            Votes = 0,
            IsWinner = false
        };

        _db.EmployeesOfMonth.Add(nomination);
        await _db.SaveChangesAsync();
        return nomination;
    }

    public async Task<List<object>> GetEmployeeOfMonthAsync(int month, int year)
    {
        return await _db.EmployeesOfMonth
            .Where(e => e.Month == month && e.Year == year)
            .OrderByDescending(e => e.Votes)
            .Select(e => (object)new
            {
                e.Id,
                e.EmployeeId,
                FullName = e.Employee.FirstName + " " + e.Employee.LastName,
                PhotoUrl = e.Employee.PhotoUrl,
                e.Votes,
                e.IsWinner,
                NominatedBy = e.NominatedBy != null
                    ? e.NominatedBy.FirstName + " " + e.NominatedBy.LastName
                    : null,
                e.Month,
                e.Year
            })
            .ToListAsync();
    }

    public async Task<EmployeeOfMonth> VoteForNomineeAsync(int employeeOfMonthId)
    {
        var nomination = await _db.EmployeesOfMonth.FindAsync(employeeOfMonthId)
            ?? throw new KeyNotFoundException("Nomination not found.");

        nomination.Votes++;
        await _db.SaveChangesAsync();
        return nomination;
    }
}
