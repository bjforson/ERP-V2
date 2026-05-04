using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class Survey : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public bool IsAnonymous { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public SurveyStatus Status { get; set; } = SurveyStatus.Draft;

    public int CreatedById { get; set; }

    // Navigation
    public new NickHR.Core.Entities.Core.Employee CreatedBy { get; set; } = null!;
    public ICollection<SurveyQuestion> Questions { get; set; } = new List<SurveyQuestion>();
    public ICollection<SurveyResponse> Responses { get; set; } = new List<SurveyResponse>();
}
