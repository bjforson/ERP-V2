using NickHR.Core.Enums;

namespace NickHR.Core.DTOs.Recruitment;

public class CandidateDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public ApplicationStage CurrentStage { get; set; }
    public DateTime ApplicationDate { get; set; }
    public decimal? Score { get; set; }
    public string? ReferredBy { get; set; }
}
