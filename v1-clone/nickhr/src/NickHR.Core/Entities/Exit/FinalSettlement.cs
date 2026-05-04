using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Exit;

public class FinalSettlement : BaseEntity
{
    public int SeparationId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal LeaveEncashment { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ProRatedBonus { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GratuityAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal LoanRecovery { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OtherDeductions { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSettlement { get; set; }

    public DateTime ProcessedAt { get; set; }

    [MaxLength(200)]
    public string? ProcessedBy { get; set; }

    [MaxLength(100)]
    public string? PaymentReference { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Separation Separation { get; set; } = null!;
}
