using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.DTOs.Leave;

public class CreateLeaveRequestDto
{
    [Required]
    public int LeavePolicyId { get; set; }

    [Required]
    public DateTime StartDate { get; set; }

    [Required]
    public DateTime EndDate { get; set; }

    [MaxLength(1000)]
    public string? Reason { get; set; }

    [MaxLength(1000)]
    public string? MedicalCertificatePath { get; set; }

    [MaxLength(2000)]
    public string? HandoverNotes { get; set; }
}
