using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class AssetAssignment : BaseEntity
{
    public int AssetId { get; set; }

    public int EmployeeId { get; set; }

    public DateTime AssignedDate { get; set; }

    public DateTime? ReturnedDate { get; set; }

    public int AssignedById { get; set; }

    [MaxLength(100)]
    public string? Condition { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Asset Asset { get; set; } = null!;

    public Employee Employee { get; set; } = null!;

    public Employee AssignedBy { get; set; } = null!;
}
