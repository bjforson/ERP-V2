using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Payroll;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Expense;

public class ExpenseClaimService : IExpenseClaimService
{
    private readonly NickHRDbContext _db;

    public ExpenseClaimService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<ExpenseClaimDto> SubmitAsync(int employeeId, ExpenseCategory category, string description, decimal amount, string? receiptPath, string? notes)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive.");

        var claim = new ExpenseClaim
        {
            EmployeeId = employeeId,
            ClaimDate = DateTime.UtcNow,
            Category = category,
            Description = description,
            Amount = amount,
            ReceiptPath = receiptPath,
            Notes = notes,
            Status = ExpenseClaimStatus.Submitted,
            ApprovedAmount = 0
        };

        _db.ExpenseClaims.Add(claim);
        await _db.SaveChangesAsync();

        return await ProjectAsync(claim.Id);
    }

    public async Task<List<ExpenseClaimDto>> GetMyClaimsAsync(int employeeId)
    {
        var ids = await _db.ExpenseClaims
            .Where(c => c.EmployeeId == employeeId && !c.IsDeleted)
            .OrderByDescending(c => c.ClaimDate)
            .Select(c => c.Id)
            .ToListAsync();

        var result = new List<ExpenseClaimDto>();
        foreach (var id in ids)
            result.Add(await ProjectAsync(id));
        return result;
    }

    public async Task<List<ExpenseClaimDto>> GetForReviewAsync(ExpenseClaimStatus? status = null)
    {
        var query = _db.ExpenseClaims.Where(c => !c.IsDeleted);
        if (status.HasValue)
            query = query.Where(c => c.Status == status.Value);
        else
            query = query.Where(c => c.Status == ExpenseClaimStatus.Submitted || c.Status == ExpenseClaimStatus.UnderReview);

        var ids = await query.OrderByDescending(c => c.ClaimDate).Select(c => c.Id).ToListAsync();
        var result = new List<ExpenseClaimDto>();
        foreach (var id in ids)
            result.Add(await ProjectAsync(id));
        return result;
    }

    public async Task<ExpenseClaimDto> ApproveAsync(int claimId, int approverId, decimal approvedAmount)
    {
        var claim = await GetClaimAsync(claimId);
        if (claim.Status == ExpenseClaimStatus.Paid || claim.Status == ExpenseClaimStatus.Rejected)
            throw new InvalidOperationException($"Cannot approve a claim with status {claim.Status}.");
        if (approvedAmount <= 0)
            throw new ArgumentException("Approved amount must be positive.");

        claim.Status = ExpenseClaimStatus.Approved;
        claim.ApprovedAmount = approvedAmount;
        claim.ApprovedById = approverId;
        claim.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await ProjectAsync(claimId);
    }

    public async Task<ExpenseClaimDto> RejectAsync(int claimId, int rejectedById, string reason)
    {
        var claim = await GetClaimAsync(claimId);
        if (claim.Status == ExpenseClaimStatus.Paid)
            throw new InvalidOperationException("Cannot reject a paid claim.");

        claim.Status = ExpenseClaimStatus.Rejected;
        claim.RejectionReason = reason;
        claim.ApprovedById = rejectedById;
        claim.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await ProjectAsync(claimId);
    }

    public async Task<ExpenseClaimDto> MarkPaidAsync(int claimId, string paymentReference)
    {
        var claim = await GetClaimAsync(claimId);
        if (claim.Status != ExpenseClaimStatus.Approved)
            throw new InvalidOperationException($"Claim must be approved before marking as paid (current: {claim.Status}).");

        claim.Status = ExpenseClaimStatus.Paid;
        claim.PaymentReference = paymentReference;
        claim.PaidAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await ProjectAsync(claimId);
    }

    private async Task<ExpenseClaim> GetClaimAsync(int id)
    {
        return await _db.ExpenseClaims.FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted)
            ?? throw new KeyNotFoundException($"Expense claim {id} not found.");
    }

    private async Task<ExpenseClaimDto> ProjectAsync(int id)
    {
        var c = await _db.ExpenseClaims
            .Include(x => x.Employee)
            .Include(x => x.ApprovedBy)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted)
            ?? throw new KeyNotFoundException($"Expense claim {id} not found.");

        return new ExpenseClaimDto(
            c.Id,
            c.EmployeeId,
            $"{c.Employee.FirstName} {c.Employee.LastName}",
            c.ClaimDate,
            c.Category,
            c.Description,
            c.Amount,
            c.ReceiptPath,
            c.Status,
            c.ApprovedAmount,
            c.ApprovedById,
            c.ApprovedBy != null ? $"{c.ApprovedBy.FirstName} {c.ApprovedBy.LastName}" : null,
            c.ApprovedAt,
            c.RejectionReason,
            c.PaymentReference,
            c.PaidAt,
            c.Notes,
            c.CreatedAt
        );
    }
}
