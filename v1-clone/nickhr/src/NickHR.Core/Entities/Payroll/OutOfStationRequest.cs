using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class OutOfStationRequest : BaseEntity
{
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    [MaxLength(200)]
    public string Destination { get; set; } = string.Empty;

    public OutOfStationDestType DestinationType { get; set; }

    [MaxLength(500)]
    public string Purpose { get; set; } = string.Empty;

    public DateTime DepartureDate { get; set; }
    public DateTime ReturnDate { get; set; }
    public int NumberOfDays { get; set; }
    public int NumberOfNights { get; set; }
    public TransportMode TransportMode { get; set; }

    // Calculated amounts
    [Column(TypeName = "decimal(18,2)")]
    public decimal AccommodationTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FeedingTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TransportTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MiscellaneousTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAllowance { get; set; }

    // Approval
    public OutOfStationStatus Status { get; set; } = OutOfStationStatus.Pending;

    public int? ApprovedById { get; set; }
    public Employee? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    // Settlement
    [Column(TypeName = "decimal(18,2)")]
    public decimal AdvancePaid { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? ActualExpenses { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? SettlementAmount { get; set; }

    public DateTime? SettledAt { get; set; }

    // JSON array of receipt file paths
    public string? ReceiptPaths { get; set; }

    public string? Notes { get; set; }
}
