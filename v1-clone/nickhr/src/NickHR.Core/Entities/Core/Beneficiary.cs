using System.ComponentModel.DataAnnotations;
using NickHR.Core.Enums;

namespace NickHR.Core.Entities.Core;

/// <summary>
/// SSNIT/Pension beneficiary nomination.
/// Per Ghana law, only blood relations, spouses, or legal dependents can be nominated.
/// </summary>
public class Beneficiary : BaseEntity
{
    public int EmployeeId { get; set; }

    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Relationship { get; set; } = string.Empty; // Spouse, Child, Parent, Sibling, etc.

    public DateTime? DateOfBirth { get; set; }

    public Gender? Gender { get; set; }

    [MaxLength(20)]
    public string? Phone { get; set; }

    [MaxLength(50)]
    public string? GhanaCardNumber { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    /// <summary>Percentage of benefit allocated to this beneficiary (must sum to 100% across all).</summary>
    public decimal PercentageShare { get; set; }

    /// <summary>Whether this is the primary beneficiary.</summary>
    public bool IsPrimary { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? Notes { get; set; }

    // Navigation
    public Employee Employee { get; set; } = null!;
}
