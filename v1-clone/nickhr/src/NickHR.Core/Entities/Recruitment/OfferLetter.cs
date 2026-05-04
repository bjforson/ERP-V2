using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Recruitment;

public class OfferLetter : BaseEntity
{
    public int ApplicationId { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OfferedSalary { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime ExpiryDate { get; set; }

    /// <summary>Draft, Sent, Accepted, Rejected, or Withdrawn</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Draft";

    [MaxLength(500)]
    public string? TemplatePath { get; set; }

    [MaxLength(500)]
    public string? GeneratedFilePath { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime? RespondedAt { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Application Application { get; set; } = null!;
}
