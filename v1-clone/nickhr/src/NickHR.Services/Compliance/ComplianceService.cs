using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.System;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Compliance;

public class ComplianceService
{
    private readonly NickHRDbContext _db;

    public ComplianceService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<List<ComplianceDeadline>> GetUpcomingAsync(int days = 90)
    {
        var cutoff = DateTime.UtcNow.AddDays(days);
        return await _db.Set<ComplianceDeadline>()
            .Where(c => c.DueDate <= cutoff)
            .OrderBy(c => c.DueDate)
            .ToListAsync();
    }

    public async Task<List<ComplianceDeadline>> GetAllAsync()
    {
        return await _db.Set<ComplianceDeadline>()
            .OrderBy(c => c.DueDate)
            .ToListAsync();
    }

    public async Task MarkCompleteAsync(int id, int completedById)
    {
        var item = await _db.Set<ComplianceDeadline>().FindAsync(id)
            ?? throw new KeyNotFoundException("Compliance deadline not found.");

        item.IsCompleted = true;
        item.CompletedById = completedById;
        item.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task SeedDefaultsAsync(int year)
    {
        var existing = await _db.Set<ComplianceDeadline>()
            .AnyAsync(c => c.DueDate.Year == year);
        if (existing) return;

        var deadlines = new List<ComplianceDeadline>();

        for (int month = 1; month <= 12; month++)
        {
            // SSNIT contribution - due 14th of following month
            var ssnitDue = new DateTime(year, month, 1).AddMonths(1);
            ssnitDue = new DateTime(ssnitDue.Year, ssnitDue.Month, Math.Min(14, DateTime.DaysInMonth(ssnitDue.Year, ssnitDue.Month)));
            deadlines.Add(new ComplianceDeadline
            {
                Title = $"SSNIT Contribution - {new DateTime(year, month, 1):MMM yyyy}",
                DueDate = ssnitDue,
                Frequency = "Monthly",
                Category = "SSNIT",
                Description = "Submit SSNIT employee and employer contributions"
            });

            // GRA PAYE - due 15th of following month
            var payeDue = new DateTime(year, month, 1).AddMonths(1);
            payeDue = new DateTime(payeDue.Year, payeDue.Month, Math.Min(15, DateTime.DaysInMonth(payeDue.Year, payeDue.Month)));
            deadlines.Add(new ComplianceDeadline
            {
                Title = $"GRA PAYE Filing - {new DateTime(year, month, 1):MMM yyyy}",
                DueDate = payeDue,
                Frequency = "Monthly",
                Category = "GRA",
                Description = "Submit monthly PAYE tax returns to GRA"
            });
        }

        // Annual return - April 30
        deadlines.Add(new ComplianceDeadline
        {
            Title = $"GRA Annual Return - {year}",
            DueDate = new DateTime(year + 1, 4, 30),
            Frequency = "Annual",
            Category = "GRA",
            Description = "Submit annual PAYE return to GRA for the tax year"
        });

        _db.Set<ComplianceDeadline>().AddRange(deadlines);
        await _db.SaveChangesAsync();
    }

    public async Task<ComplianceDeadline> CreateAsync(ComplianceDeadline deadline)
    {
        _db.Set<ComplianceDeadline>().Add(deadline);
        await _db.SaveChangesAsync();
        return deadline;
    }
}
