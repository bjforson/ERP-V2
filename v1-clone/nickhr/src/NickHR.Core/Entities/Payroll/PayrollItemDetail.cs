using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class PayrollItemDetail : BaseEntity
{
    public int PayrollItemId { get; set; }

    public int SalaryComponentId { get; set; }

    [Required]
    [MaxLength(200)]
    public string ComponentName { get; set; } = string.Empty;

    /// <summary>Earning or Deduction</summary>
    [Required]
    [MaxLength(20)]
    public string ComponentType { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    // Navigation Properties
    public PayrollItem PayrollItem { get; set; } = null!;

    public SalaryComponent SalaryComponent { get; set; } = null!;
}
