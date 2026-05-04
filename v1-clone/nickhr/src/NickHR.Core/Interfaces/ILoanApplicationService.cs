using NickHR.Core.Enums;

namespace NickHR.Core.Interfaces;

// DTO records kept inline – no separate DTO file needed for these thin projections.
public record LoanApplicationDto(
    int Id,
    int EmployeeId,
    string EmployeeName,
    LoanApplicationType LoanType,
    decimal RequestedAmount,
    string Purpose,
    int RepaymentMonths,
    decimal MonthlyInstallment,
    decimal InterestRate,
    decimal TotalRepayable,
    int? GuarantorEmployeeId,
    LoanApplicationStatus Status,
    int? ManagerApprovedById,
    DateTime? ManagerApprovedAt,
    int? HRApprovedById,
    DateTime? HRApprovedAt,
    int? FinanceApprovedById,
    DateTime? FinanceApprovedAt,
    int? RejectedById,
    string? RejectionReason,
    DateTime? DisbursedAt,
    string? DisbursementReference,
    string? SupportingDocumentPath,
    int? LoanId,
    DateTime CreatedAt
);

public record LoanEligibilityDto(
    bool IsEligible,
    string? IneligibilityReason,
    decimal MaxLoanAmount,
    decimal BasicSalary,
    int ActiveLoansCount,
    decimal TotalOutstandingBalance,
    int TenureMonths
);

public interface ILoanApplicationService
{
    Task<LoanApplicationDto> ApplyForLoanAsync(
        int employeeId,
        LoanApplicationType loanType,
        decimal amount,
        string purpose,
        int repaymentMonths,
        int? guarantorEmployeeId = null,
        string? documentPath = null);

    Task<List<LoanApplicationDto>> GetMyApplicationsAsync(int employeeId);
    Task<List<LoanApplicationDto>> GetPendingApprovalsAsync(LoanApplicationStatus? status);
    Task<LoanApplicationDto> ManagerApproveAsync(int applicationId, int approverId);
    Task<LoanApplicationDto> HRApproveAsync(int applicationId, int approverId);
    Task<LoanApplicationDto> FinanceApproveAsync(int applicationId, int approverId);
    Task<LoanApplicationDto> RejectAsync(int applicationId, int rejectedById, string reason);
    Task<LoanApplicationDto> DisburseAsync(int applicationId, string reference);
    Task<LoanEligibilityDto> GetEligibilityAsync(int employeeId);
}
