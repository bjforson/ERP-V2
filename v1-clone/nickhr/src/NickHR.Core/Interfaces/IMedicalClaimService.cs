using NickHR.Core.Enums;

namespace NickHR.Core.Interfaces;

public record MedicalClaimDto(
    int Id,
    int EmployeeId,
    string EmployeeName,
    DateTime ClaimDate,
    MedicalClaimCategory Category,
    string Description,
    string? ProviderName,
    DateTime? ReceiptDate,
    decimal ClaimAmount,
    decimal ApprovedAmount,
    MedicalClaimStatus Status,
    int? ReviewedById,
    DateTime? ReviewedAt,
    int? ApprovedById,
    DateTime? ApprovedAt,
    string? RejectionReason,
    MedicalPaymentMethod? PaymentMethod,
    string? PaymentReference,
    DateTime? PaidAt,
    string? ReceiptPaths,
    DateTime CreatedAt
);

public record MedicalBalanceDto(
    int EmployeeId,
    int Year,
    decimal AnnualLimit,
    decimal TotalApproved,
    decimal RemainingBalance
);

public record MedicalBenefitDto(
    int Id,
    string Name,
    decimal AnnualLimit,
    string? CategoryLimits,
    int WaitingPeriodMonths,
    bool CoversDependents,
    string? Description,
    bool IsActive
);

public interface IMedicalClaimService
{
    Task<MedicalClaimDto> SubmitClaimAsync(
        int employeeId,
        MedicalClaimCategory category,
        string description,
        string? providerName,
        DateTime? receiptDate,
        decimal claimAmount,
        string? receiptPaths = null);

    Task<(List<MedicalClaimDto> Claims, MedicalBalanceDto Balance)> GetMyClaimsAsync(int employeeId, int? year);
    Task<List<MedicalClaimDto>> GetClaimsForReviewAsync(MedicalClaimStatus? status);
    Task<MedicalClaimDto> ReviewClaimAsync(int claimId, int reviewerId);
    Task<MedicalClaimDto> ApproveClaimAsync(int claimId, int approverId, decimal approvedAmount, MedicalPaymentMethod paymentMethod);
    Task<MedicalClaimDto> RejectClaimAsync(int claimId, int rejectedById, string reason);
    Task<MedicalClaimDto> MarkAsPaidAsync(int claimId, string paymentReference);
    Task<MedicalBalanceDto> GetEmployeeBalanceAsync(int employeeId, int year);
    Task<MedicalBenefitDto?> GetBenefitPolicyAsync();
    Task<MedicalBenefitDto> UpdateBenefitPolicyAsync(string name, decimal annualLimit, int waitingPeriodMonths, bool coversDependents);
}
