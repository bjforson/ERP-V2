using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class Dependent : BaseEntity
{
    public int EmployeeId { get; set; }

    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Relationship { get; set; }

    public DateTime? DateOfBirth { get; set; }

    public Gender Gender { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation Properties
    public Employee Employee { get; set; } = null!;
}
