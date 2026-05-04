using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Recruitment;

public class Interview : BaseEntity
{
    public int ApplicationId { get; set; }

    public int InterviewerId { get; set; }

    public DateTime ScheduledAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Phone, InPerson, or Video</summary>
    [Required]
    [MaxLength(20)]
    public string InterviewType { get; set; } = string.Empty;

    [Column(TypeName = "decimal(5,2)")]
    public decimal? OverallScore { get; set; }

    [MaxLength(200)]
    public string? Recommendation { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Application Application { get; set; } = null!;

    public Employee Interviewer { get; set; } = null!;
}
