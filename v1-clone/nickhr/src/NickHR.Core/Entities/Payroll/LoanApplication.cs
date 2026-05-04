using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;

namespace NickHR.Core.Entities.Payroll;

public class LoanApplication : BaseEntity
{
    public int EmployeeId { get; set; }

    public LoanApplicationType LoanType { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RequestedAmount { get; set; }

    [MaxLength(500)]
    public string Purpose { get; set; } = string.Empty;

    public int RepaymentMonths { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MonthlyInstallment { get; set; }

    [Column(TypeName = "decimal(8,4)")]
    public decimal InterestRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalRepayable { get; set; }

    // Guarantor (for larger loans)
    public int? GuarantorEmployeeId { get; set; }
    public bool GuarantorApproved { get; set; }

    // Multi-level approval
    public LoanApplicationStatus Status { get; set; } = LoanApplicationStatus.Pending;

    public int? ManagerApprovedById { get; set; }
    public DateTime? ManagerApprovedAt { get; set; }

    public int? HRApprovedById { get; set; }
    public DateTime? HRApprovedAt { get; set; }

    public int? FinanceApprovedById { get; set; }
    public DateTime? FinanceApprovedAt { get; set; }

    public int? RejectedById { get; set; }
    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    public DateTime? DisbursedAt { get; set; }
    [MaxLength(200)]
    public string? DisbursementReference { get; set; }

    [MaxLength(500)]
    public string? SupportingDocumentPath { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Link to created Loan entity after final approval
    public int? LoanId { get; set; }

    // Navigation
    public Employee Employee { get; set; } = null!;
    public Employee? GuarantorEmployee { get; set; }
    public Loan? Loan { get; set; }
}
