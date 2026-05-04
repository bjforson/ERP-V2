using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Recruitment;

public class JobPosting : BaseEntity
{
    public int JobRequisitionId { get; set; }

    /// <summary>Internal, External, or Both</summary>
    [Required]
    [MaxLength(20)]
    public string PostedOn { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime? PublishedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    [MaxLength(500)]
    public string? ExternalUrl { get; set; }

    // Navigation Properties
    public JobRequisition JobRequisition { get; set; } = null!;
}
