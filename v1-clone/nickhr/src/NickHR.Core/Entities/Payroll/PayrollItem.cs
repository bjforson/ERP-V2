using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class PayrollItem : BaseEntity
{
    public int PayrollRunId { get; set; }

    public int EmployeeId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal BasicSalary { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAllowances { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossPay { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal SSNITEmployee { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal SSNITEmployer { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TaxableIncome { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PAYE { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalDeductions { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetPay { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal OvertimeHours { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OvertimePay { get; set; }

    // Navigation Properties
    public PayrollRun PayrollRun { get; set; } = null!;

    public Employee Employee { get; set; } = null!;

    public ICollection<PayrollItemDetail> PayrollItemDetails { get; set; } = new List<PayrollItemDetail>();
}
