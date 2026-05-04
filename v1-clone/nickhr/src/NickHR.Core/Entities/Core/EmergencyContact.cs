using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class EmergencyContact : BaseEntity
{
    public int EmployeeId { get; set; }

    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Relationship { get; set; }

    [Required]
    [MaxLength(20)]
    public string PrimaryPhone { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? SecondaryPhone { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;
}
