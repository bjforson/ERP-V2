using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class Loan : BaseEntity
{
    public int EmployeeId { get; set; }

    [Required]
    [MaxLength(100)]
    public string LoanType { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Column(TypeName = "decimal(10,4)")]
    public decimal InterestRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalRepayable { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MonthlyInstallment { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal BalanceRemaining { get; set; }

    public LoanStatus LoanStatus { get; set; }

    [MaxLength(200)]
    public string? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public ICollection<LoanRepayment> LoanRepayments { get; set; } = new List<LoanRepayment>();
}
