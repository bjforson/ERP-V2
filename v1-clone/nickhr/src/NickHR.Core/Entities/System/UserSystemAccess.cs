using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

/// <summary>
/// Tracks which external systems each NickHR ApplicationUser has access to,
/// along with the role in that system. Used by the unified user provisioning flow
/// to call external system APIs (e.g., NSCIS) when access is granted/revoked.
/// </summary>
public class UserSystemAccess
{
    public int Id { get; set; }

    /// <summary>
    /// ApplicationUser.Id (ASP.NET Identity uses string)
    /// </summary>
    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the external system, e.g. "NSCIS".
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string SystemName { get; set; } = string.Empty;

    /// <summary>
    /// External user ID returned by the target system (e.g., NSCIS User.Id).
    /// Stored as string for flexibility.
    /// </summary>
    [MaxLength(100)]
    public string? ExternalUserId { get; set; }

    /// <summary>
    /// Role ID in the external system.
    /// </summary>
    public int? RoleId { get; set; }

    /// <summary>
    /// Cached display name of the role (for UI display without extra API call).
    /// </summary>
    [MaxLength(100)]
    public string? RoleName { get; set; }

    /// <summary>
    /// Whether the user currently has active access to this system.
    /// Set to false on revocation without deleting the record (for audit).
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Last time this record was synced with the external system.
    /// </summary>
    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(200)]
    public string? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [MaxLength(200)]
    public string? UpdatedBy { get; set; }
}
