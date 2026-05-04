using NickHR.Core.Entities.Core;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Exit;

public class ClearanceItem : BaseEntity
{
    public int SeparationId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Department { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsCleared { get; set; }

    public int? ClearedById { get; set; }

    public DateTime? ClearedAt { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Separation Separation { get; set; } = null!;

    public Employee? ClearedBy { get; set; }
}
