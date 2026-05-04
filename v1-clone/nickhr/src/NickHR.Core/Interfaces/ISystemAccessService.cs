using NickHR.Core.Entities.System;

namespace NickHR.Core.Interfaces;

/// <summary>
/// Orchestrates granting, updating and revoking access to external systems
/// (e.g., NSCIS) from NickHR. Delegates to ICentralAuthClient for API calls
/// and persists UserSystemAccess records locally.
/// </summary>
public interface ISystemAccessService
{
    /// <summary>
    /// Gets all system access records for a user.
    /// </summary>
    Task<List<UserSystemAccess>> GetUserAccessAsync(string userId);

    /// <summary>
    /// Grants a user access to an external system with a specific role.
    /// If the user already has access, updates the role.
    /// Calls NSCIS API to provision the user there.
    /// </summary>
    Task<SystemAccessResult> GrantAccessAsync(string userId, string systemName, int roleId, string? actor = null);

    /// <summary>
    /// Revokes a user's access to an external system.
    /// Calls NSCIS API to deactivate the user there (soft delete).
    /// </summary>
    Task<SystemAccessResult> RevokeAccessAsync(string userId, string systemName, string? actor = null);

    /// <summary>
    /// Gets the list of NSCIS roles for populating UI dropdowns.
    /// </summary>
    Task<List<NscisRole>> GetNscisRolesAsync();
}

public class SystemAccessResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? TemporaryPassword { get; set; }
    public int? ExternalUserId { get; set; }
}
