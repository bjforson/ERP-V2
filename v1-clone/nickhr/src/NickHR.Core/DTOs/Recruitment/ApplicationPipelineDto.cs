namespace NickHR.Core.DTOs.Recruitment;

public class ApplicationPipelineDto
{
    public int Applied { get; set; }
    public int Screening { get; set; }
    public int Interview { get; set; }
    public int Offer { get; set; }
    public int Hired { get; set; }
    public int Rejected { get; set; }
}
