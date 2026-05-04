using NickHR.Core.Enums;

namespace NickHR.Core.DTOs.Recruitment;

public class JobRequisitionDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string? Designation { get; set; }
    public string? Grade { get; set; }
    public int NumberOfPositions { get; set; }
    public int FilledPositions { get; set; }
    public JobRequisitionStatus Status { get; set; }
    public string? RequestedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
