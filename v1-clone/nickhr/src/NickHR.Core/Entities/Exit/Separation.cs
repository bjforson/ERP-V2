using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Exit;

public class Separation : BaseEntity
{
    public int EmployeeId { get; set; }

    public SeparationType SeparationType { get; set; }

    public DateTime NoticeDate { get; set; }

    public DateTime LastWorkingDate { get; set; }

    public int NoticePeriodDays { get; set; }

    [MaxLength(1000)]
    public string? Reason { get; set; }

    public int? ApprovedById { get; set; }

    public DateTime? ApprovedAt { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;

    public Employee? ApprovedBy { get; set; }

    public ICollection<ClearanceItem> ClearanceItems { get; set; } = new List<ClearanceItem>();

    public FinalSettlement? FinalSettlement { get; set; }
}
