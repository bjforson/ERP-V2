using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Training;

public class TrainingAttendance : BaseEntity
{
    public int TrainingProgramId { get; set; }

    public int EmployeeId { get; set; }

    /// <summary>Enrolled, Attended, Completed, or NoShow</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "Enrolled";

    [MaxLength(300)]
    public string? CertificationName { get; set; }

    public DateTime? CertificationExpiryDate { get; set; }

    [MaxLength(1000)]
    public string? Feedback { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? Score { get; set; }

    // Navigation Properties
    public TrainingProgram TrainingProgram { get; set; } = null!;

    public Employee Employee { get; set; } = null!;
}
