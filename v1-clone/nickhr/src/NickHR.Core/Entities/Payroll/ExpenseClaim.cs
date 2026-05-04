using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class ExpenseClaim : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public DateTime ClaimDate { get; set; } = DateTime.UtcNow;
    public ExpenseCategory Category { get; set; }
    public string Description { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public string? ReceiptPath { get; set; }
    public ExpenseClaimStatus Status { get; set; } = ExpenseClaimStatus.Submitted;

    [Column(TypeName = "decimal(18,2)")]
    public decimal ApprovedAmount { get; set; }

    public int? ApprovedById { get; set; }
    public Employee? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public string? RejectionReason { get; set; }
    public string? PaymentReference { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? Notes { get; set; }
}
