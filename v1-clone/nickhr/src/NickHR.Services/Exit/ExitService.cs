using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Exit;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Exit;

public interface IExitService
{
    Task<Separation> InitiateSeparationAsync(int employeeId, SeparationType separationType,
        string? reason, DateTime lastWorkingDate, int noticePeriodDays);
    Task<List<Separation>> GetSeparationsAsync(SeparationType? filter = null);
    Task<Separation?> GetSeparationByIdAsync(int id);
    Task<Separation> ApproveSeparationAsync(int id, int approvedById);
    Task<ClearanceItem> UpdateClearanceItemAsync(int clearanceItemId, bool isCleared,
        int clearedById, string? notes);
    Task<ExitInterview> RecordExitInterviewAsync(int separationId, int interviewerId,
        string? reasonForLeaving, bool? wouldRecommend, string? feedback,
        int? overallExperience, string? suggestions);
    Task<FinalSettlement> ProcessFinalSettlementAsync(int separationId, decimal leaveEncashment,
        decimal proRatedBonus, decimal gratuity, decimal loanRecovery,
        decimal otherDeductions, string? paymentRef);
}

public class ExitService : IExitService
{
    private readonly NickHRDbContext _db;

    public ExitService(NickHRDbContext db)
    {
        _db = db;
    }

    // ─── Separation ──────────────────────────────────────────────────────────

    public async Task<Separation> InitiateSeparationAsync(int employeeId, SeparationType separationType,
        string? reason, DateTime lastWorkingDate, int noticePeriodDays)
    {
        var separation = new Separation
        {
            EmployeeId = employeeId,
            SeparationType = separationType,
            Reason = reason,
            NoticeDate = DateTime.UtcNow,
            LastWorkingDate = lastWorkingDate,
            NoticePeriodDays = noticePeriodDays
        };

        _db.Separations.Add(separation);
        await _db.SaveChangesAsync();

        // Auto-create standard clearance items
        var departments = new[] { "IT", "Finance", "Admin", "Department" };
        foreach (var dept in departments)
        {
            _db.ClearanceItems.Add(new ClearanceItem
            {
                SeparationId = separation.Id,
                Department = dept,
                Description = $"{dept} clearance required before separation.",
                IsCleared = false
            });
        }

        await _db.SaveChangesAsync();
        return separation;
    }

    public async Task<List<Separation>> GetSeparationsAsync(SeparationType? filter = null)
    {
        var query = _db.Separations
            .Where(s => !s.IsDeleted)
            .Include(s => s.Employee)
            .AsQueryable();

        if (filter.HasValue)
            query = query.Where(s => s.SeparationType == filter.Value);

        return await query.OrderByDescending(s => s.NoticeDate).ToListAsync();
    }

    public async Task<Separation?> GetSeparationByIdAsync(int id)
    {
        return await _db.Separations
            .Where(s => s.Id == id && !s.IsDeleted)
            .Include(s => s.Employee)
            .Include(s => s.ClearanceItems)
                .ThenInclude(c => c.ClearedBy)
            .Include(s => s.FinalSettlement)
            .FirstOrDefaultAsync();
    }

    public async Task<Separation> ApproveSeparationAsync(int id, int approvedById)
    {
        var separation = await _db.Separations.FindAsync(id)
            ?? throw new KeyNotFoundException($"Separation {id} not found.");

        separation.ApprovedById = approvedById;
        separation.ApprovedAt = DateTime.UtcNow;
        separation.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return separation;
    }

    // ─── Clearance ───────────────────────────────────────────────────────────

    public async Task<ClearanceItem> UpdateClearanceItemAsync(int clearanceItemId, bool isCleared,
        int clearedById, string? notes)
    {
        var item = await _db.ClearanceItems.FindAsync(clearanceItemId)
            ?? throw new KeyNotFoundException($"Clearance item {clearanceItemId} not found.");

        item.IsCleared = isCleared;
        item.ClearedById = clearedById;
        item.ClearedAt = isCleared ? DateTime.UtcNow : null;
        item.Notes = notes;
        item.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return item;
    }

    // ─── Exit Interview ──────────────────────────────────────────────────────

    public async Task<ExitInterview> RecordExitInterviewAsync(int separationId, int interviewerId,
        string? reasonForLeaving, bool? wouldRecommend, string? feedback,
        int? overallExperience, string? suggestions)
    {
        var separation = await _db.Separations.FindAsync(separationId)
            ?? throw new KeyNotFoundException($"Separation {separationId} not found.");

        var existing = await _db.ExitInterviews
            .FirstOrDefaultAsync(e => e.SeparationId == separationId && !e.IsDeleted);

        if (existing is not null)
            throw new InvalidOperationException("An exit interview has already been recorded for this separation.");

        var interview = new ExitInterview
        {
            SeparationId = separationId,
            InterviewDate = DateTime.UtcNow,
            InterviewerId = interviewerId,
            ReasonForLeaving = reasonForLeaving,
            WouldRecommend = wouldRecommend,
            Feedback = feedback,
            OverallExperience = overallExperience,
            Suggestions = suggestions
        };

        _db.ExitInterviews.Add(interview);
        await _db.SaveChangesAsync();
        return interview;
    }

    // ─── Final Settlement ────────────────────────────────────────────────────

    public async Task<FinalSettlement> ProcessFinalSettlementAsync(int separationId,
        decimal leaveEncashment, decimal proRatedBonus, decimal gratuity,
        decimal loanRecovery, decimal otherDeductions, string? paymentRef)
    {
        var separation = await _db.Separations
            .Include(s => s.Employee)
            .FirstOrDefaultAsync(s => s.Id == separationId && !s.IsDeleted)
            ?? throw new KeyNotFoundException($"Separation {separationId} not found.");

        var existing = await _db.FinalSettlements
            .FirstOrDefaultAsync(f => f.SeparationId == separationId && !f.IsDeleted);

        if (existing is not null)
            throw new InvalidOperationException("A final settlement has already been processed for this separation.");

        var totalSettlement = leaveEncashment + proRatedBonus + gratuity - loanRecovery - otherDeductions;

        var settlement = new FinalSettlement
        {
            SeparationId = separationId,
            LeaveEncashment = leaveEncashment,
            ProRatedBonus = proRatedBonus,
            GratuityAmount = gratuity,
            LoanRecovery = loanRecovery,
            OtherDeductions = otherDeductions,
            TotalSettlement = totalSettlement,
            ProcessedAt = DateTime.UtcNow,
            PaymentReference = paymentRef
        };

        _db.FinalSettlements.Add(settlement);

        // Update employee status based on separation type
        var employee = separation.Employee;
        employee.EmploymentStatus = separation.SeparationType switch
        {
            SeparationType.Resignation => EmploymentStatus.Resigned,
            SeparationType.Termination => EmploymentStatus.Terminated,
            SeparationType.Retirement => EmploymentStatus.Retired,
            SeparationType.Deceased => EmploymentStatus.Deceased,
            SeparationType.ContractEnd => EmploymentStatus.Terminated,
            SeparationType.Redundancy => EmploymentStatus.Terminated,
            _ => EmploymentStatus.Terminated
        };
        employee.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return settlement;
    }
}
