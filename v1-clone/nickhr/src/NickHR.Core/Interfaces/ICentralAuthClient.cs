namespace NickHR.Core.Interfaces;

/// <summary>
/// Client for interacting with the NSCIS central auth and user provisioning API.
/// NSCIS is the single source of truth for user credentials and the target system
/// for unified user provisioning from NickHR.
/// </summary>
public interface ICentralAuthClient
{
    /// <summary>
    /// Validates a username or email + password against the NSCIS central auth endpoint.
    /// </summary>
    Task<CentralAuthResult> ValidateCredentialsAsync(string usernameOrEmail, string password);

    /// <summary>
    /// Gets the list of active roles in NSCIS for use in role dropdowns.
    /// </summary>
    Task<List<NscisRole>> GetRolesAsync();

    /// <summary>
    /// Creates or updates a user in NSCIS (idempotent by username).
    /// Returns provisioned user details including a temporary password if newly created.
    /// </summary>
    Task<ProvisionResult> ProvisionUserAsync(ProvisionUserRequest request);

    /// <summary>
    /// Deactivates a user in NSCIS (soft delete via IsActive=false).
    /// </summary>
    Task<bool> DeactivateUserAsync(string username);
}

public class CentralAuthResult
{
    public bool IsValid { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public int NscisUserId { get; set; }
}

public class NscisRole
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystemRole { get; set; }
}

public class ProvisionUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int? RoleId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ProvisionResult
{
    public bool Success { get; set; }
    public int NscisUserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool Created { get; set; }
    public string? TemporaryPassword { get; set; }
    public string? ErrorMessage { get; set; }
}
