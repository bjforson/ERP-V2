namespace NickHR.Core.Enums;

public enum LoanApplicationStatus
{
    Pending,
    ManagerApproved,
    HRApproved,
    FinanceApproved,
    Disbursed,
    Rejected,
    Cancelled
}

public enum LoanApplicationType
{
    SalaryAdvance,
    StaffLoan,
    EmergencyLoan
}
