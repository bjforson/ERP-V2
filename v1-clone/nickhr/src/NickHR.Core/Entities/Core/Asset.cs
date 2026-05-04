using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Core;

public class Asset : BaseEntity
{
    [Required]
    [MaxLength(100)]
    public string AssetTag { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public AssetCategory Category { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? SerialNumber { get; set; }

    public DateTime? PurchaseDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PurchasePrice { get; set; }

    [MaxLength(50)]
    public string Condition { get; set; } = "New";

    public int? AssignedToEmployeeId { get; set; }

    public DateTime? AssignedDate { get; set; }

    public DateTime? ReturnDate { get; set; }

    public AssetStatus Status { get; set; } = AssetStatus.Available;

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Employee? AssignedToEmployee { get; set; }

    public ICollection<AssetAssignment> Assignments { get; set; } = new List<AssetAssignment>();
}
