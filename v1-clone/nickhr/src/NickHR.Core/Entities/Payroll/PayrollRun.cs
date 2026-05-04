using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class PayrollRun : BaseEntity
{
    public int PayPeriodMonth { get; set; }

    public int PayPeriodYear { get; set; }

    public DateTime RunDate { get; set; } = DateTime.UtcNow;

    public PayrollStatus Status { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalGrossPay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalNetPay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSSNITEmployee { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSSNITEmployer { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalPAYE { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalDeductions { get; set; }

    [MaxLength(200)]
    public string? ProcessedBy { get; set; }

    [MaxLength(200)]
    public string? ApprovedBy { get; set; }

    public DateTime? ApprovedAt { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public ICollection<PayrollItem> PayrollItems { get; set; } = new List<PayrollItem>();
}
