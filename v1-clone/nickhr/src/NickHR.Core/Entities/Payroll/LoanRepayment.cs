using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class LoanRepayment : BaseEntity
{
    public int LoanId { get; set; }

    public int? PayrollRunId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime RepaymentDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal BalanceAfter { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Loan Loan { get; set; } = null!;

    public PayrollRun? PayrollRun { get; set; }
}
