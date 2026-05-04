using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Medical;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Medical;

public class MedicalClaimService : IMedicalClaimService
{
    private readonly NickHRDbContext _db;

    public MedicalClaimService(NickHRDbContext db)
    {
        _db = db;
    }

    // -------------------------------------------------------------------------
    // Submit
    // -------------------------------------------------------------------------
    public async Task<MedicalClaimDto> SubmitClaimAsync(
        int employeeId,
        MedicalClaimCategory category,
        string description,
        string? providerName,
        DateTime? receiptDate,
        decimal claimAmount,
        string? receiptPaths = null)
    {
        if (claimAmount <= 0)
            throw new ArgumentException("Claim amount must be positive.");

        var policy = await EnsurePolicyExistsAsync();

        var year = DateTime.UtcNow.Year;
        var balance = await GetEmployeeBalanceAsync(employeeId, year);

        if (balance.RemainingBalance <= 0)
            throw new InvalidOperationException(
                $"Annual medical limit of {policy.AnnualLimit:N2} has been reached for {year}.");

        if (claimAmount > balance.RemainingBalance)
            throw new InvalidOperationException(
                $"Claim amount ({claimAmount:N2}) exceeds remaining annual balance ({balance.RemainingBalance:N2}).");

        var claim = new MedicalClaim
        {
            EmployeeId = employeeId,
            ClaimDate = DateTime.UtcNow,
            Category = category,
            Description = description,
            ProviderName = providerName,
            ReceiptDate = receiptDate,
            ClaimAmount = claimAmount,
            ApprovedAmount = 0,
            Status = MedicalClaimStatus.Submitted,
            ReceiptPaths = receiptPaths
        };

        _db.MedicalClaims.Add(claim);
        await _db.SaveChangesAsync();

        return await ProjectAsync(claim.Id);
    }

    // -------------------------------------------------------------------------
    // Queries
    // -------------------------------------------------------------------------
    public async Task<(List<MedicalClaimDto> Claims, MedicalBalanceDto Balance)> GetMyClaimsAsync(int employeeId, int? year)
    {
        var targetYear = year ?? DateTime.UtcNow.Year;

        var ids = await _db.MedicalClaims
            .Where(c => c.EmployeeId == employeeId && !c.IsDeleted && c.ClaimDate.Year == targetYear)
            .OrderByDescending(c => c.ClaimDate)
            .Select(c => c.Id)
            .ToListAsync();

        var claims = new List<MedicalClaimDto>();
        foreach (var id in ids)
            claims.Add(await ProjectAsync(id));

        var balance = await GetEmployeeBalanceAsync(employeeId, targetYear);
        return (claims, balance);
    }

    public async Task<List<MedicalClaimDto>> GetClaimsForReviewAsync(MedicalClaimStatus? status)
    {
        var query = _db.MedicalClaims.Where(c => !c.IsDeleted);

        query = status.HasValue
            ? query.Where(c => c.Status == status.Value)
            : query.Where(c => c.Status == MedicalClaimStatus.Submitted || c.Status == MedicalClaimStatus.UnderReview);

        var ids = await query.OrderByDescending(c => c.ClaimDate).Select(c => c.Id).ToListAsync();

        var result = new List<MedicalClaimDto>();
        foreach (var id in ids)
            result.Add(await ProjectAsync(id));
        return result;
    }

    // -------------------------------------------------------------------------
    // Review / Approve / Reject / Pay
    // -------------------------------------------------------------------------
    public async Task<MedicalClaimDto> ReviewClaimAsync(int claimId, int reviewerId)
    {
        var claim = await GetClaimAsync(claimId);
        if (claim.Status != MedicalClaimStatus.Submitted)
            throw new InvalidOperationException($"Claim is not in Submitted status (current: {claim.Status}).");

        claim.Status = MedicalClaimStatus.UnderReview;
        claim.ReviewedById = reviewerId;
        claim.ReviewedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await ProjectAsync(claimId);
    }

    public async Task<MedicalClaimDto> ApproveClaimAsync(int claimId, int approverId, decimal approvedAmount, MedicalPaymentMethod paymentMethod)
    {
        var claim = await GetClaimAsync(claimId);
        if (claim.Status != MedicalClaimStatus.UnderReview && claim.Status != MedicalClaimStatus.Submitted)
            throw new InvalidOperationException($"Claim cannot be approved from status {claim.Status}.");

        if (approvedAmount <= 0)
            throw new ArgumentException("Approved amount must be positive.");

        claim.ApprovedAmount = approvedAmount;
        claim.ApprovedById = approverId;
        claim.ApprovedAt = DateTime.UtcNow;
        claim.PaymentMethod = paymentMethod;
        claim.Status = approvedAmount >= claim.ClaimAmount
            ? MedicalClaimStatus.Approved
            : MedicalClaimStatus.PartiallyApproved;
        await _db.SaveChangesAsync();

        return await ProjectAsync(claimId);
    }

    public async Task<MedicalClaimDto> RejectClaimAsync(int claimId, int rejectedById, string reason)
    {
        var claim = await GetClaimAsync(claimId);
        if (claim.Status == MedicalClaimStatus.Paid)
            throw new InvalidOperationException("Cannot reject a paid claim.");

        claim.Status = MedicalClaimStatus.Rejected;
        claim.RejectionReason = reason;
        claim.ReviewedById = rejectedById;
        claim.ReviewedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await ProjectAsync(claimId);
    }

    public async Task<MedicalClaimDto> MarkAsPaidAsync(int claimId, string paymentReference)
    {
        var claim = await GetClaimAsync(claimId);
        if (claim.Status != MedicalClaimStatus.Approved && claim.Status != MedicalClaimStatus.PartiallyApproved)
            throw new InvalidOperationException($"Claim must be approved before marking as paid (current: {claim.Status}).");

        claim.Status = MedicalClaimStatus.Paid;
        claim.PaymentReference = paymentReference;
        claim.PaidAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await ProjectAsync(claimId);
    }

    // -------------------------------------------------------------------------
    // Balance & Policy
    // -------------------------------------------------------------------------
    public async Task<MedicalBalanceDto> GetEmployeeBalanceAsync(int employeeId, int year)
    {
        var policy = await EnsurePolicyExistsAsync();

        var totalApproved = await _db.MedicalClaims
            .Where(c => c.EmployeeId == employeeId
                        && !c.IsDeleted
                        && c.ClaimDate.Year == year
                        && (c.Status == MedicalClaimStatus.Approved
                            || c.Status == MedicalClaimStatus.PartiallyApproved
                            || c.Status == MedicalClaimStatus.Paid))
            .SumAsync(c => (decimal?)c.ApprovedAmount) ?? 0m;

        return new MedicalBalanceDto(
            employeeId,
            year,
            policy.AnnualLimit,
            totalApproved,
            policy.AnnualLimit - totalApproved
        );
    }

    public async Task<MedicalBenefitDto?> GetBenefitPolicyAsync()
    {
        var benefit = await _db.MedicalBenefits
            .Where(b => b.IsActive && !b.IsDeleted)
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync();

        return benefit is null ? null : MapBenefit(benefit);
    }

    public async Task<MedicalBenefitDto> UpdateBenefitPolicyAsync(
        string name, decimal annualLimit, int waitingPeriodMonths, bool coversDependents)
    {
        var benefit = await _db.MedicalBenefits
            .Where(b => b.IsActive && !b.IsDeleted)
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync();

        if (benefit is null)
        {
            benefit = new MedicalBenefit
            {
                Name = name,
                AnnualLimit = annualLimit,
                WaitingPeriodMonths = waitingPeriodMonths,
                CoversDependents = coversDependents,
                IsActive = true
            };
            _db.MedicalBenefits.Add(benefit);
        }
        else
        {
            benefit.Name = name;
            benefit.AnnualLimit = annualLimit;
            benefit.WaitingPeriodMonths = waitingPeriodMonths;
            benefit.CoversDependents = coversDependents;
        }

        await _db.SaveChangesAsync();
        return MapBenefit(benefit);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------
    private async Task<MedicalBenefit> EnsurePolicyExistsAsync()
    {
        var benefit = await _db.MedicalBenefits
            .Where(b => b.IsActive && !b.IsDeleted)
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync();

        if (benefit is not null)
            return benefit;

        // Seed default
        benefit = new MedicalBenefit
        {
            Name = "Annual Medical Allowance",
            AnnualLimit = 5000m,
            WaitingPeriodMonths = 3,
            CoversDependents = false,
            IsActive = true
        };
        _db.MedicalBenefits.Add(benefit);
        await _db.SaveChangesAsync();
        return benefit;
    }

    private async Task<MedicalClaim> GetClaimAsync(int id)
    {
        return await _db.MedicalClaims.FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted)
            ?? throw new KeyNotFoundException($"Medical claim {id} not found.");
    }

    private async Task<MedicalClaimDto> ProjectAsync(int id)
    {
        var c = await _db.MedicalClaims
            .Include(x => x.Employee)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted)
            ?? throw new KeyNotFoundException($"Medical claim {id} not found.");

        return new MedicalClaimDto(
            c.Id,
            c.EmployeeId,
            $"{c.Employee.FirstName} {c.Employee.LastName}",
            c.ClaimDate,
            c.Category,
            c.Description,
            c.ProviderName,
            c.ReceiptDate,
            c.ClaimAmount,
            c.ApprovedAmount,
            c.Status,
            c.ReviewedById,
            c.ReviewedAt,
            c.ApprovedById,
            c.ApprovedAt,
            c.RejectionReason,
            c.PaymentMethod,
            c.PaymentReference,
            c.PaidAt,
            c.ReceiptPaths,
            c.CreatedAt
        );
    }

    private static MedicalBenefitDto MapBenefit(MedicalBenefit b) => new(
        b.Id, b.Name, b.AnnualLimit, b.CategoryLimits,
        b.WaitingPeriodMonths, b.CoversDependents, b.Description, b.IsActive);
}
