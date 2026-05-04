using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class SurveyAnswer : BaseEntity
{
    public int SurveyResponseId { get; set; }

    public int SurveyQuestionId { get; set; }

    [MaxLength(2000)]
    public string? AnswerText { get; set; }

    public int? Rating { get; set; }

    // Navigation
    public SurveyResponse SurveyResponse { get; set; } = null!;
    public SurveyQuestion SurveyQuestion { get; set; } = null!;
}
