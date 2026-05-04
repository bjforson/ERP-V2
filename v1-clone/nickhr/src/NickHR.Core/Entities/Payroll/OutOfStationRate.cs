using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Payroll;

public class OutOfStationRate : BaseEntity
{
    public int GradeId { get; set; }
    public Grade Grade { get; set; } = null!;

    public OutOfStationDestType DestinationType { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal AccommodationRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FeedingRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TransportRoadRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TransportAirRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MiscellaneousRate { get; set; }

    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
}
