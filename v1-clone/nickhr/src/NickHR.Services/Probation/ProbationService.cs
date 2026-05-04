using Microsoft.EntityFrameworkCore;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;
using EmployeeEntity = NickHR.Core.Entities.Core.Employee;
using ProbationReviewEntity = NickHR.Core.Entities.Core.ProbationReview;

namespace NickHR.Services.Probation;

public interface IProbationService
{
    Task<List<EmployeeEntity>> GetEmployeesApproachingProbationEndAsync(int daysThreshold = 30);
    Task<ProbationReviewEntity> CreateReviewAsync(int employeeId, DateTime probationEndDate, int reviewedById);
    Task<ProbationReviewEntity> CompleteReviewAsync(int reviewId, ProbationDecision decision, int? extensionMonths, string managerComments, string hrComments);
    Task<List<ProbationReviewEntity>> GetReviewsAsync(string? status = null);
}

public class ProbationService : IProbationService
{
    private readonly NickHRDbContext _db;

    public ProbationService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<List<EmployeeEntity>> GetEmployeesApproachingProbationEndAsync(int daysThreshold = 30)
    {
        var thresholdDate = DateTime.UtcNow.AddDays(daysThreshold);

        return await _db.Employees
            .Where(e => !e.IsDeleted
                && e.ProbationEndDate.HasValue
                && e.ProbationEndDate.Value <= thresholdDate
                && e.ProbationEndDate.Value >= DateTime.UtcNow.Date
                && (e.EmploymentStatus == EmploymentStatus.OnProbation || e.EmploymentStatus == EmploymentStatus.Active))
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .OrderBy(e => e.ProbationEndDate)
            .ToListAsync();
    }

    public async Task<ProbationReviewEntity> CreateReviewAsync(int employeeId, DateTime probationEndDate, int reviewedById)
    {
        _ = await _db.Employees.FindAsync(employeeId)
            ?? throw new KeyNotFoundException($"Employee {employeeId} not found.");

        _ = await _db.Employees.FindAsync(reviewedById)
            ?? throw new KeyNotFoundException($"Reviewer employee {reviewedById} not found.");

        var existingPending = await _db.ProbationReviews.AnyAsync(p =>
            p.EmployeeId == employeeId && p.Status == nameof(ProbationStatus.Pending) && !p.IsDeleted);

        if (existingPending)
            throw new InvalidOperationException("A pending probation review already exists for this employee.");

        var review = new ProbationReviewEntity
        {
            EmployeeId = employeeId,
            ReviewDate = DateTime.UtcNow,
            ProbationEndDate = probationEndDate,
            ReviewedById = reviewedById,
            Status = nameof(ProbationStatus.Pending)
        };

        _db.ProbationReviews.Add(review);
        await _db.SaveChangesAsync();
        return review;
    }

    public async Task<ProbationReviewEntity> CompleteReviewAsync(
        int reviewId,
        ProbationDecision decision,
        int? extensionMonths,
        string managerComments,
        string hrComments)
    {
        var review = await _db.ProbationReviews
            .Include(p => p.Employee)
            .FirstOrDefaultAsync(p => p.Id == reviewId && !p.IsDeleted)
            ?? throw new KeyNotFoundException($"Probation review {reviewId} not found.");

        if (review.Status != nameof(ProbationStatus.Pending))
            throw new InvalidOperationException("Only pending reviews can be completed.");

        review.Decision = decision;
        review.ManagerComments = managerComments;
        review.HRComments = hrComments;
        review.ReviewedAt = DateTime.UtcNow;
        review.Status = nameof(ProbationStatus.Completed);

        var employee = review.Employee;

        switch (decision)
        {
            case ProbationDecision.Confirm:
                employee.EmploymentStatus = EmploymentStatus.Confirmed;
                employee.ConfirmationDate = DateTime.UtcNow;
                break;

            case ProbationDecision.Extend:
                if (!extensionMonths.HasValue || extensionMonths <= 0)
                    throw new ArgumentException("Extension months must be specified and positive for an extension decision.");

                review.ExtensionMonths = extensionMonths;
                var newEndDate = review.ProbationEndDate.AddMonths(extensionMonths.Value);
                review.NewProbationEndDate = newEndDate;
                employee.ProbationEndDate = newEndDate;
                break;

            case ProbationDecision.Terminate:
                employee.EmploymentStatus = EmploymentStatus.Terminated;
                break;
        }

        await _db.SaveChangesAsync();
        return review;
    }

    public async Task<List<ProbationReviewEntity>> GetReviewsAsync(string? status = null)
    {
        var query = _db.ProbationReviews
            .Include(p => p.Employee)
                .ThenInclude(e => e.Department)
            .Include(p => p.ReviewedBy)
            .Where(p => !p.IsDeleted);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(p => p.Status == status);

        return await query.OrderByDescending(p => p.ReviewDate).ToListAsync();
    }
}
