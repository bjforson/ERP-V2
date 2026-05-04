using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Recruitment;

public class JobRequisition : BaseEntity
{
    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    public int DepartmentId { get; set; }

    public int? DesignationId { get; set; }

    public int? GradeId { get; set; }

    public int NumberOfPositions { get; set; } = 1;

    [Column(TypeName = "decimal(18,2)")]
    public decimal? SalaryRangeMin { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? SalaryRangeMax { get; set; }

    public string? Description { get; set; }

    public string? Requirements { get; set; }

    public JobRequisitionStatus Status { get; set; }

    public int RequestedById { get; set; }

    public int? ApprovedById { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public DateTime? ClosingDate { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Department Department { get; set; } = null!;

    public Designation? Designation { get; set; }

    public Grade? Grade { get; set; }

    public Employee RequestedBy { get; set; } = null!;

    public Employee? ApprovedBy { get; set; }

    public ICollection<JobPosting> JobPostings { get; set; } = new List<JobPosting>();

    public ICollection<Application> Applications { get; set; } = new List<Application>();
}
