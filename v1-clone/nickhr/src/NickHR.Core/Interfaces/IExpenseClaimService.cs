using NickHR.Core.Enums;

namespace NickHR.Core.Interfaces;

public record ExpenseClaimDto(
    int Id,
    int EmployeeId,
    string EmployeeName,
    DateTime ClaimDate,
    ExpenseCategory Category,
    string Description,
    decimal Amount,
    string? ReceiptPath,
    ExpenseClaimStatus Status,
    decimal ApprovedAmount,
    int? ApprovedById,
    string? ApprovedByName,
    DateTime? ApprovedAt,
    string? RejectionReason,
    string? PaymentReference,
    DateTime? PaidAt,
    string? Notes,
    DateTime CreatedAt
);

public interface IExpenseClaimService
{
    Task<ExpenseClaimDto> SubmitAsync(int employeeId, ExpenseCategory category, string description, decimal amount, string? receiptPath, string? notes);
    Task<List<ExpenseClaimDto>> GetMyClaimsAsync(int employeeId);
    Task<List<ExpenseClaimDto>> GetForReviewAsync(ExpenseClaimStatus? status = null);
    Task<ExpenseClaimDto> ApproveAsync(int claimId, int approverId, decimal approvedAmount);
    Task<ExpenseClaimDto> RejectAsync(int claimId, int rejectedById, string reason);
    Task<ExpenseClaimDto> MarkPaidAsync(int claimId, string paymentReference);
}
