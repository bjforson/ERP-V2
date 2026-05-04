using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Performance;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Performance;

public interface IPerformanceService
{
    // Appraisal Cycles
    Task<AppraisalCycle> CreateCycleAsync(string name, DateTime startDate, DateTime endDate, string? description);
    Task<List<AppraisalCycle>> GetCyclesAsync();
    Task<AppraisalCycle?> GetActiveCycleAsync();

    // Goals
    Task<Goal> CreateGoalAsync(int employeeId, int appraisalCycleId, string title, string? description,
        string? targetValue, decimal weight, DateTime? dueDate);
    Task<List<Goal>> GetEmployeeGoalsAsync(int employeeId, int? cycleId = null);
    Task<Goal> UpdateGoalProgressAsync(int goalId, decimal progressPercent, string? achievedValue, string status);
    Task DeleteGoalAsync(int goalId);

    // Appraisals
    Task<AppraisalForm> CreateAppraisalAsync(int appraisalCycleId, int employeeId, int reviewerId);
    Task<List<AppraisalForm>> GetAppraisalsAsync(int? cycleId = null, int? employeeId = null);
    Task<AppraisalForm> SubmitSelfRatingAsync(int appraisalId, decimal rating, string? comments);
    Task<AppraisalForm> SubmitManagerRatingAsync(int appraisalId, decimal managerRating, decimal finalRating, string? comments);
    Task<AppraisalForm?> GetAppraisalByIdAsync(int id);

    // Low Performers
    Task<List<AppraisalForm>> GetLowPerformersAsync(int cycleId, decimal threshold);
}

public class PerformanceService : IPerformanceService
{
    private readonly NickHRDbContext _db;

    public PerformanceService(NickHRDbContext db)
    {
        _db = db;
    }

    // ─── Appraisal Cycles ────────────────────────────────────────────────────

    public async Task<AppraisalCycle> CreateCycleAsync(string name, DateTime startDate, DateTime endDate, string? description)
    {
        var cycle = new AppraisalCycle
        {
            Name = name,
            StartDate = startDate,
            EndDate = endDate,
            Description = description,
            IsActive = true
        };

        _db.AppraisalCycles.Add(cycle);
        await _db.SaveChangesAsync();
        return cycle;
    }

    public async Task<List<AppraisalCycle>> GetCyclesAsync()
    {
        return await _db.AppraisalCycles
            .Where(c => !c.IsDeleted)
            .OrderByDescending(c => c.StartDate)
            .ToListAsync();
    }

    public async Task<AppraisalCycle?> GetActiveCycleAsync()
    {
        var today = DateTime.UtcNow;
        return await _db.AppraisalCycles
            .Where(c => !c.IsDeleted && c.IsActive && c.StartDate <= today && c.EndDate >= today)
            .OrderByDescending(c => c.StartDate)
            .FirstOrDefaultAsync();
    }

    // ─── Goals ───────────────────────────────────────────────────────────────

    public async Task<Goal> CreateGoalAsync(int employeeId, int appraisalCycleId, string title,
        string? description, string? targetValue, decimal weight, DateTime? dueDate)
    {
        var goal = new Goal
        {
            EmployeeId = employeeId,
            AppraisalCycleId = appraisalCycleId,
            Title = title,
            Description = description,
            TargetValue = targetValue,
            Weight = weight,
            DueDate = dueDate,
            ProgressPercent = 0,
            Status = "NotStarted"
        };

        _db.Goals.Add(goal);
        await _db.SaveChangesAsync();
        return goal;
    }

    public async Task<List<Goal>> GetEmployeeGoalsAsync(int employeeId, int? cycleId = null)
    {
        var query = _db.Goals
            .Where(g => !g.IsDeleted && g.EmployeeId == employeeId);

        if (cycleId.HasValue)
            query = query.Where(g => g.AppraisalCycleId == cycleId.Value);

        return await query
            .Include(g => g.AppraisalCycle)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();
    }

    public async Task<Goal> UpdateGoalProgressAsync(int goalId, decimal progressPercent, string? achievedValue, string status)
    {
        var goal = await _db.Goals.FindAsync(goalId)
            ?? throw new KeyNotFoundException($"Goal {goalId} not found.");

        goal.ProgressPercent = progressPercent;
        goal.AchievedValue = achievedValue;
        goal.Status = status;
        goal.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return goal;
    }

    public async Task DeleteGoalAsync(int goalId)
    {
        var goal = await _db.Goals.FindAsync(goalId)
            ?? throw new KeyNotFoundException($"Goal {goalId} not found.");

        goal.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ─── Appraisals ──────────────────────────────────────────────────────────

    public async Task<AppraisalForm> CreateAppraisalAsync(int appraisalCycleId, int employeeId, int reviewerId)
    {
        var form = new AppraisalForm
        {
            AppraisalCycleId = appraisalCycleId,
            EmployeeId = employeeId,
            ReviewerId = reviewerId,
            Status = AppraisalStatus.NotStarted
        };

        _db.AppraisalForms.Add(form);
        await _db.SaveChangesAsync();
        return form;
    }

    public async Task<List<AppraisalForm>> GetAppraisalsAsync(int? cycleId = null, int? employeeId = null)
    {
        var query = _db.AppraisalForms
            .Where(f => !f.IsDeleted)
            .Include(f => f.Employee)
            .Include(f => f.Reviewer)
            .Include(f => f.AppraisalCycle)
            .AsQueryable();

        if (cycleId.HasValue)
            query = query.Where(f => f.AppraisalCycleId == cycleId.Value);

        if (employeeId.HasValue)
            query = query.Where(f => f.EmployeeId == employeeId.Value);

        return await query.OrderByDescending(f => f.CreatedAt).ToListAsync();
    }

    public async Task<AppraisalForm> SubmitSelfRatingAsync(int appraisalId, decimal rating, string? comments)
    {
        var form = await _db.AppraisalForms.FindAsync(appraisalId)
            ?? throw new KeyNotFoundException($"Appraisal {appraisalId} not found.");

        form.SelfRating = rating;
        form.SelfComments = comments;
        form.Status = AppraisalStatus.PendingReview;
        form.SubmittedAt = DateTime.UtcNow;
        form.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return form;
    }

    public async Task<AppraisalForm> SubmitManagerRatingAsync(int appraisalId, decimal managerRating,
        decimal finalRating, string? comments)
    {
        var form = await _db.AppraisalForms.FindAsync(appraisalId)
            ?? throw new KeyNotFoundException($"Appraisal {appraisalId} not found.");

        form.ManagerRating = managerRating;
        form.FinalRating = finalRating;
        form.ManagerComments = comments;
        form.Status = AppraisalStatus.Completed;
        form.ReviewedAt = DateTime.UtcNow;
        form.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return form;
    }

    public async Task<AppraisalForm?> GetAppraisalByIdAsync(int id)
    {
        return await _db.AppraisalForms
            .Where(f => f.Id == id && !f.IsDeleted)
            .Include(f => f.Employee)
            .Include(f => f.Reviewer)
            .Include(f => f.AppraisalCycle)
                .ThenInclude(c => c.AppraisalForms)
            .FirstOrDefaultAsync();
    }

    // ─── Low Performers ──────────────────────────────────────────────────────

    public async Task<List<AppraisalForm>> GetLowPerformersAsync(int cycleId, decimal threshold)
    {
        return await _db.AppraisalForms
            .Where(f => !f.IsDeleted
                && f.AppraisalCycleId == cycleId
                && f.Status == AppraisalStatus.Completed
                && f.FinalRating.HasValue
                && f.FinalRating < threshold)
            .Include(f => f.Employee)
            .OrderBy(f => f.FinalRating)
            .ToListAsync();
    }
}
