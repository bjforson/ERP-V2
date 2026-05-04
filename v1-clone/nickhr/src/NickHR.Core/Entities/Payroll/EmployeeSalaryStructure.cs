using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class EmployeeSalaryStructure : BaseEntity
{
    public int EmployeeId { get; set; }

    public int SalaryComponentId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public SalaryComponent SalaryComponent { get; set; } = null!;
}
