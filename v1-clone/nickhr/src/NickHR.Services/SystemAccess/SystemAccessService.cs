using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickHR.Core.Entities.System;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.SystemAccess;

/// <summary>
/// Orchestrates cross-system user provisioning. Calls ICentralAuthClient for API
/// operations and persists UserSystemAccess records locally.
/// </summary>
public class SystemAccessService : ISystemAccessService
{
    private readonly NickHRDbContext _dbContext;
    private readonly ICentralAuthClient _centralAuth;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<SystemAccessService> _logger;

    public const string SystemNameNscis = "NSCIS";

    public SystemAccessService(
        NickHRDbContext dbContext,
        ICentralAuthClient centralAuth,
        UserManager<ApplicationUser> userManager,
        ILogger<SystemAccessService> logger)
    {
        _dbContext = dbContext;
        _centralAuth = centralAuth;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<List<UserSystemAccess>> GetUserAccessAsync(string userId)
    {
        return await _dbContext.UserSystemAccesses
            .Where(a => a.UserId == userId)
            .ToListAsync();
    }

    public async Task<List<NscisRole>> GetNscisRolesAsync()
    {
        return await _centralAuth.GetRolesAsync();
    }

    public async Task<SystemAccessResult> GrantAccessAsync(string userId, string systemName, int roleId, string? actor = null)
    {
        try
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return new SystemAccessResult { Success = false, ErrorMessage = "User not found in NickHR" };
            }

            // Look up existing access record
            var existing = await _dbContext.UserSystemAccesses
                .FirstOrDefaultAsync(a => a.UserId == userId && a.SystemName == systemName);

            // For NSCIS: call the provisioning API
            if (systemName == SystemNameNscis)
            {
                var roles = await _centralAuth.GetRolesAsync();
                var role = roles.FirstOrDefault(r => r.Id == roleId);
                var roleName = role?.DisplayName ?? role?.Name ?? "Unknown";

                // Derive a stable username for NSCIS: prefer email prefix, fall back to email
                var username = DeriveUsername(user);

                var provision = await _centralAuth.ProvisionUserAsync(new ProvisionUserRequest
                {
                    Username = username,
                    Email = user.Email ?? string.Empty,
                    FirstName = user.FirstName ?? string.Empty,
                    LastName = user.LastName ?? string.Empty,
                    RoleId = roleId,
                    IsActive = true
                });

                if (!provision.Success)
                {
                    return new SystemAccessResult
                    {
                        Success = false,
                        ErrorMessage = provision.ErrorMessage ?? "NSCIS provisioning failed"
                    };
                }

                // Persist or update the UserSystemAccess record
                if (existing == null)
                {
                    existing = new UserSystemAccess
                    {
                        UserId = userId,
                        SystemName = systemName,
                        ExternalUserId = provision.NscisUserId.ToString(),
                        RoleId = roleId,
                        RoleName = roleName,
                        IsActive = true,
                        LastSyncedAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = actor ?? "System"
                    };
                    _dbContext.UserSystemAccesses.Add(existing);
                }
                else
                {
                    existing.ExternalUserId = provision.NscisUserId.ToString();
                    existing.RoleId = roleId;
                    existing.RoleName = roleName;
                    existing.IsActive = true;
                    existing.LastSyncedAt = DateTime.UtcNow;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.UpdatedBy = actor ?? "System";
                }

                await _dbContext.SaveChangesAsync();

                _logger.LogInformation("Granted {System} access to user {UserId} with role {RoleName} by {Actor}",
                    systemName, userId, roleName, actor);

                return new SystemAccessResult
                {
                    Success = true,
                    ExternalUserId = provision.NscisUserId,
                    TemporaryPassword = provision.TemporaryPassword
                };
            }

            return new SystemAccessResult
            {
                Success = false,
                ErrorMessage = $"Unknown system: {systemName}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GrantAccess failed for user {UserId} system {System}", userId, systemName);
            return new SystemAccessResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<SystemAccessResult> RevokeAccessAsync(string userId, string systemName, string? actor = null)
    {
        try
        {
            var existing = await _dbContext.UserSystemAccesses
                .FirstOrDefaultAsync(a => a.UserId == userId && a.SystemName == systemName);

            if (existing == null || !existing.IsActive)
            {
                return new SystemAccessResult { Success = true }; // Already revoked/never had access
            }

            if (systemName == SystemNameNscis)
            {
                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    return new SystemAccessResult { Success = false, ErrorMessage = "User not found" };
                }

                var username = DeriveUsername(user);
                var deactivated = await _centralAuth.DeactivateUserAsync(username);

                if (!deactivated)
                {
                    _logger.LogWarning("NSCIS deactivation failed for {Username}, still marking local record inactive", username);
                    // Continue anyway — mark local record as inactive even if remote call failed
                }
            }

            existing.IsActive = false;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = actor ?? "System";
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Revoked {System} access for user {UserId} by {Actor}",
                systemName, userId, actor);

            return new SystemAccessResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RevokeAccess failed for user {UserId} system {System}", userId, systemName);
            return new SystemAccessResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Derives a stable NSCIS username from a NickHR ApplicationUser.
    /// NSCIS max username length is 50 chars, must be unique.
    /// Uses the email prefix (part before @) truncated to 50 chars.
    /// </summary>
    private static string DeriveUsername(ApplicationUser user)
    {
        var email = user.Email ?? user.UserName ?? string.Empty;
        var atIndex = email.IndexOf('@');
        var prefix = atIndex > 0 ? email.Substring(0, atIndex) : email;

        // Sanitize: keep only letters, digits, dots, underscores, hyphens
        prefix = new string(prefix.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-').ToArray());

        if (string.IsNullOrEmpty(prefix))
        {
            prefix = $"user_{user.Id.Substring(0, Math.Min(8, user.Id.Length))}";
        }

        return prefix.Length > 50 ? prefix.Substring(0, 50) : prefix;
    }
}
