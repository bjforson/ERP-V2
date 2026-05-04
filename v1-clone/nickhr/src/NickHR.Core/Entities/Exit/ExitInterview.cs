using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Exit;

public class ExitInterview : BaseEntity
{
    public int SeparationId { get; set; }

    public DateTime InterviewDate { get; set; }

    public int InterviewerId { get; set; }

    [MaxLength(500)]
    public string? ReasonForLeaving { get; set; }

    public bool? WouldRecommend { get; set; }

    public string? Feedback { get; set; }

    /// <summary>Overall experience rating from 1 to 5</summary>
    [Range(1, 5)]
    public int? OverallExperience { get; set; }

    public string? Suggestions { get; set; }

    // Navigation Properties
    public Separation Separation { get; set; } = null!;

    public Employee Interviewer { get; set; } = null!;
}
