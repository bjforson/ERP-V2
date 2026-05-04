using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class SurveyQuestion : BaseEntity
{
    public int SurveyId { get; set; }

    [Required]
    [MaxLength(500)]
    public string QuestionText { get; set; } = string.Empty;

    public SurveyQuestionType QuestionType { get; set; }

    /// <summary>JSON array of option strings for MultipleChoice questions.</summary>
    public string? Options { get; set; }

    public int OrderIndex { get; set; }

    // Navigation
    public Survey Survey { get; set; } = null!;
}
