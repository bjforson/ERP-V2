using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Core;

public class TravelRequest : BaseEntity
{
    public int EmployeeId { get; set; }

    [Required, MaxLength(300)]
    public string Destination { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Purpose { get; set; }

    public DateTime DepartureDate { get; set; }

    public DateTime ReturnDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal EstimatedCost { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AdvanceRequested { get; set; }

    public TravelRequestStatus Status { get; set; } = TravelRequestStatus.Pending;

    public int? ApprovedById { get; set; }

    public DateTime? ApprovedAt { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? ActualCost { get; set; }

    [MaxLength(1000)]
    public string? ReconciliationNotes { get; set; }

    // Navigation
    public Employee Employee { get; set; } = null!;
    public Employee? ApprovedBy { get; set; }
}
