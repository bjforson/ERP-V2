using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Recruitment;

public class Application : BaseEntity
{
    public int JobRequisitionId { get; set; }

    public int CandidateId { get; set; }

    public DateTime ApplicationDate { get; set; } = DateTime.UtcNow;

    public ApplicationStage Stage { get; set; }

    [MaxLength(500)]
    public string? CoverLetterPath { get; set; }

    [MaxLength(500)]
    public string? CVPath { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public JobRequisition JobRequisition { get; set; } = null!;

    public Candidate Candidate { get; set; } = null!;

    public ICollection<Interview> InterviewScores { get; set; } = new List<Interview>();
}
