using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;

namespace NickHR.Core.Entities.Medical;

public class MedicalClaim : BaseEntity
{
    public int EmployeeId { get; set; }

    public DateTime ClaimDate { get; set; }

    public MedicalClaimCategory Category { get; set; }

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ProviderName { get; set; }

    public DateTime? ReceiptDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ClaimAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ApprovedAmount { get; set; }

    public MedicalClaimStatus Status { get; set; } = MedicalClaimStatus.Submitted;

    public int? ReviewedById { get; set; }
    public DateTime? ReviewedAt { get; set; }

    public int? ApprovedById { get; set; }
    public DateTime? ApprovedAt { get; set; }

    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    public MedicalPaymentMethod? PaymentMethod { get; set; }
    [MaxLength(200)]
    public string? PaymentReference { get; set; }
    public DateTime? PaidAt { get; set; }

    // Receipt file paths (JSON array)
    [MaxLength(2000)]
    public string? ReceiptPaths { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation
    public Employee Employee { get; set; } = null!;
}
