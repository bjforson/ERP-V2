using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Payroll;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Loans;

public class LoanApplicationService : ILoanApplicationService
{
    private readonly NickHRDbContext _db;

    // Interest rates by loan type (annual %)
    private static readonly Dictionary<LoanApplicationType, decimal> InterestRates = new()
    {
        { LoanApplicationType.SalaryAdvance, 0m },
        { LoanApplicationType.StaffLoan, 10m },
        { LoanApplicationType.EmergencyLoan, 5m }
    };

    public LoanApplicationService(NickHRDbContext db)
    {
        _db = db;
    }

    // -------------------------------------------------------------------------
    // Apply
    // -------------------------------------------------------------------------
    public async Task<LoanApplicationDto> ApplyForLoanAsync(
        int employeeId,
        LoanApplicationType loanType,
        decimal amount,
        string purpose,
        int repaymentMonths,
        int? guarantorEmployeeId = null,
        string? documentPath = null)
    {
        var employee = await _db.Employees.FindAsync(employeeId)
            ?? throw new KeyNotFoundException($"Employee {employeeId} not found.");

        // Eligibility checks
        var eligibility = await ComputeEligibilityAsync(employee);
        if (!eligibility.IsEligible)
            throw new InvalidOperationException($"Employee is not eligible: {eligibility.IneligibilityReason}");

        if (amount <= 0)
            throw new ArgumentException("Loan amount must be positive.");

        if (amount > eligibility.MaxLoanAmount)
            throw new InvalidOperationException(
                $"Requested amount ({amount:N2}) exceeds maximum allowed ({eligibility.MaxLoanAmount:N2}).");

        if (loanType == LoanApplicationType.SalaryAdvance)
        {
            var maxAdvance = employee.BasicSalary * 0.5m;
            if (amount > maxAdvance)
                throw new InvalidOperationException(
                    $"Salary advance cannot exceed 50% of basic salary ({maxAdvance:N2}).");
        }

        if (repaymentMonths < 1)
            throw new ArgumentException("Repayment months must be at least 1.");

        var annualRate = InterestRates[loanType];
        var (monthlyInstallment, totalRepayable) = CalculateRepayment(amount, annualRate, repaymentMonths);

        var application = new LoanApplication
        {
            EmployeeId = employeeId,
            LoanType = loanType,
            RequestedAmount = amount,
            Purpose = purpose,
            RepaymentMonths = repaymentMonths,
            InterestRate = annualRate,
            MonthlyInstallment = monthlyInstallment,
            TotalRepayable = totalRepayable,
            GuarantorEmployeeId = guarantorEmployeeId,
            SupportingDocumentPath = documentPath,
            Status = LoanApplicationStatus.Pending
        };

        _db.LoanApplications.Add(application);
        await _db.SaveChangesAsync();

        return await ProjectAsync(application.Id);
    }

    // -------------------------------------------------------------------------
    // Queries
    // -------------------------------------------------------------------------
    public async Task<List<LoanApplicationDto>> GetMyApplicationsAsync(int employeeId)
    {
        var ids = await _db.LoanApplications
            .Where(a => a.EmployeeId == employeeId && !a.IsDeleted)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.Id)
            .ToListAsync();

        var result = new List<LoanApplicationDto>();
        foreach (var id in ids)
            result.Add(await ProjectAsync(id));
        return result;
    }

    public async Task<List<LoanApplicationDto>> GetPendingApprovalsAsync(LoanApplicationStatus? status)
    {
        var query = _db.LoanApplications.Where(a => !a.IsDeleted);

        query = status.HasValue
            ? query.Where(a => a.Status == status.Value)
            : query.Where(a =>
                a.Status == LoanApplicationStatus.Pending ||
                a.Status == LoanApplicationStatus.ManagerApproved ||
                a.Status == LoanApplicationStatus.HRApproved);

        var ids = await query.OrderByDescending(a => a.CreatedAt).Select(a => a.Id).ToListAsync();

        var result = new List<LoanApplicationDto>();
        foreach (var id in ids)
            result.Add(await ProjectAsync(id));
        return result;
    }

    // -------------------------------------------------------------------------
    // Approvals
    // -------------------------------------------------------------------------
    public async Task<LoanApplicationDto> ManagerApproveAsync(int applicationId, int approverId)
    {
        var app = await GetApplicationAsync(applicationId);
        if (app.Status != LoanApplicationStatus.Pending)
            throw new InvalidOperationException($"Application is not in Pending status (current: {app.Status}).");

        app.Status = LoanApplicationStatus.ManagerApproved;
        app.ManagerApprovedById = approverId;
        app.ManagerApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await ProjectAsync(applicationId);
    }

    public async Task<LoanApplicationDto> HRApproveAsync(int applicationId, int approverId)
    {
        var app = await GetApplicationAsync(applicationId);
        if (app.Status != LoanApplicationStatus.ManagerApproved)
            throw new InvalidOperationException($"Application must be Manager-approved first (current: {app.Status}).");

        app.Status = LoanApplicationStatus.HRApproved;
        app.HRApprovedById = approverId;
        app.HRApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await ProjectAsync(applicationId);
    }

    public async Task<LoanApplicationDto> FinanceApproveAsync(int applicationId, int approverId)
    {
        var app = await GetApplicationAsync(applicationId);
        if (app.Status != LoanApplicationStatus.HRApproved)
            throw new InvalidOperationException($"Application must be HR-approved first (current: {app.Status}).");

        var employee = await _db.Employees.FindAsync(app.EmployeeId)
            ?? throw new KeyNotFoundException("Employee not found.");

        // Create the Loan entity
        var loan = new Loan
        {
            EmployeeId = app.EmployeeId,
            LoanType = app.LoanType.ToString(),
            Amount = app.RequestedAmount,
            InterestRate = app.InterestRate,
            TotalRepayable = app.TotalRepayable,
            MonthlyInstallment = app.MonthlyInstallment,
            BalanceRemaining = app.TotalRepayable,
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddMonths(app.RepaymentMonths),
            LoanStatus = LoanStatus.Active,
            ApprovedBy = approverId.ToString(),
            ApprovedAt = DateTime.UtcNow
        };

        _db.Loans.Add(loan);
        await _db.SaveChangesAsync(); // get Loan.Id

        app.Status = LoanApplicationStatus.FinanceApproved;
        app.FinanceApprovedById = approverId;
        app.FinanceApprovedAt = DateTime.UtcNow;
        app.LoanId = loan.Id;
        await _db.SaveChangesAsync();

        return await ProjectAsync(applicationId);
    }

    public async Task<LoanApplicationDto> RejectAsync(int applicationId, int rejectedById, string reason)
    {
        var app = await GetApplicationAsync(applicationId);
        if (app.Status == LoanApplicationStatus.Disbursed)
            throw new InvalidOperationException("Cannot reject an already-disbursed application.");

        app.Status = LoanApplicationStatus.Rejected;
        app.RejectedById = rejectedById;
        app.RejectionReason = reason;
        await _db.SaveChangesAsync();

        return await ProjectAsync(applicationId);
    }

    public async Task<LoanApplicationDto> DisburseAsync(int applicationId, string reference)
    {
        var app = await GetApplicationAsync(applicationId);
        if (app.Status != LoanApplicationStatus.FinanceApproved)
            throw new InvalidOperationException($"Application must be Finance-approved before disbursement (current: {app.Status}).");

        app.Status = LoanApplicationStatus.Disbursed;
        app.DisbursementReference = reference;
        app.DisbursedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await ProjectAsync(applicationId);
    }

    // -------------------------------------------------------------------------
    // Eligibility
    // -------------------------------------------------------------------------
    public async Task<LoanEligibilityDto> GetEligibilityAsync(int employeeId)
    {
        var employee = await _db.Employees.FindAsync(employeeId)
            ?? throw new KeyNotFoundException($"Employee {employeeId} not found.");
        return await ComputeEligibilityAsync(employee);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------
    private async Task<LoanEligibilityDto> ComputeEligibilityAsync(NickHR.Core.Entities.Core.Employee employee)
    {
        var tenureMonths = employee.HireDate.HasValue
            ? (int)((DateTime.UtcNow - employee.HireDate.Value).TotalDays / 30.44)
            : 0;

        if (tenureMonths < 6)
            return new LoanEligibilityDto(false, "Minimum tenure of 6 months required.", 0, employee.BasicSalary, 0, 0, tenureMonths);

        var activeLoans = await _db.Loans
            .Where(l => l.EmployeeId == employee.Id && !l.IsDeleted && l.LoanStatus == LoanStatus.Active)
            .ToListAsync();

        if (activeLoans.Count >= 1)
            return new LoanEligibilityDto(false, "Employee already has an active loan.", employee.BasicSalary * 3,
                employee.BasicSalary, activeLoans.Count, activeLoans.Sum(l => l.BalanceRemaining), tenureMonths);

        var maxLoanAmount = employee.BasicSalary * 3;
        return new LoanEligibilityDto(true, null, maxLoanAmount, employee.BasicSalary, 0, 0, tenureMonths);
    }

    private static (decimal monthly, decimal total) CalculateRepayment(decimal principal, decimal annualRatePercent, int months)
    {
        if (annualRatePercent == 0m)
            return (Math.Round(principal / months, 2), principal);

        var monthlyRate = annualRatePercent / 100m / 12m;
        // Standard amortisation formula
        var factor = (decimal)Math.Pow((double)(1 + monthlyRate), months);
        var monthly = Math.Round(principal * monthlyRate * factor / (factor - 1), 2);
        return (monthly, Math.Round(monthly * months, 2));
    }

    private async Task<LoanApplication> GetApplicationAsync(int id)
    {
        return await _db.LoanApplications.FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted)
            ?? throw new KeyNotFoundException($"Loan application {id} not found.");
    }

    private async Task<LoanApplicationDto> ProjectAsync(int id)
    {
        var a = await _db.LoanApplications
            .Include(x => x.Employee)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted)
            ?? throw new KeyNotFoundException($"Loan application {id} not found.");

        return new LoanApplicationDto(
            a.Id,
            a.EmployeeId,
            $"{a.Employee.FirstName} {a.Employee.LastName}",
            a.LoanType,
            a.RequestedAmount,
            a.Purpose,
            a.RepaymentMonths,
            a.MonthlyInstallment,
            a.InterestRate,
            a.TotalRepayable,
            a.GuarantorEmployeeId,
            a.Status,
            a.ManagerApprovedById,
            a.ManagerApprovedAt,
            a.HRApprovedById,
            a.HRApprovedAt,
            a.FinanceApprovedById,
            a.FinanceApprovedAt,
            a.RejectedById,
            a.RejectionReason,
            a.DisbursedAt,
            a.DisbursementReference,
            a.SupportingDocumentPath,
            a.LoanId,
            a.CreatedAt
        );
    }
}
