namespace NickHR.Core.Entities.System;

public class SurveyResponse : BaseEntity
{
    public int SurveyId { get; set; }

    /// <summary>Null for anonymous surveys.</summary>
    public int? EmployeeId { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Survey Survey { get; set; } = null!;
    public NickHR.Core.Entities.Core.Employee? Employee { get; set; }
    public ICollection<SurveyAnswer> Answers { get; set; } = new List<SurveyAnswer>();
}
