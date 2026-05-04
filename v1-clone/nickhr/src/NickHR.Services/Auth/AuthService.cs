using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NickHR.Core.DTOs.Auth;
using NickHR.Core.Entities.System;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Auth;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IConfiguration _configuration;
    private readonly NickHRDbContext _dbContext;
    private readonly ICentralAuthClient _centralAuth;
    private readonly ILogger<AuthService> _logger;
    // Optional — only present if AddMemoryCache() ran. Used to invalidate the
    // single-session sid cache immediately after a login/register rotates the
    // canonical sid; without this the validator could briefly read a stale value
    // and reject the just-issued token.
    private readonly IMemoryCache? _sessionCache;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IConfiguration configuration,
        NickHRDbContext dbContext,
        ICentralAuthClient centralAuth,
        ILogger<AuthService> logger,
        IMemoryCache? sessionCache = null)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _configuration = configuration;
        _dbContext = dbContext;
        _centralAuth = centralAuth;
        _logger = logger;
        _sessionCache = sessionCache;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        // Step 1: Try central auth (NSCIS) first — single source of truth for credentials
        var centralResult = await _centralAuth.ValidateCredentialsAsync(request.Email, request.Password);

        ApplicationUser? user;
        bool centralAuthSucceeded = centralResult.IsValid;

        if (centralAuthSucceeded)
        {
            // Central auth succeeded — find or auto-provision the ApplicationUser
            user = await _userManager.FindByEmailAsync(centralResult.Email)
                   ?? await _userManager.FindByEmailAsync(request.Email);

            if (user == null)
            {
                _logger.LogInformation("Auto-provisioning NickHR user for {Email} from central auth", centralResult.Email);
                user = await AutoProvisionUserAsync(centralResult);
                if (user == null)
                {
                    throw new UnauthorizedAccessException("Failed to provision user account. Contact HR.");
                }
            }

            if (!user.IsActive)
                throw new UnauthorizedAccessException("Account is inactive. Please contact HR.");
        }
        else
        {
            // Fallback: use local ASP.NET Identity password check (backward compat during migration)
            user = await _userManager.FindByEmailAsync(request.Email)
                ?? throw new UnauthorizedAccessException("Invalid email or password.");

            if (!user.IsActive)
                throw new UnauthorizedAccessException("Account is inactive. Please contact HR.");

            var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

            if (result.IsLockedOut)
                throw new UnauthorizedAccessException("Account is locked out. Please try again later.");

            if (!result.Succeeded)
            {
                await LogLoginAuditAsync(user, false, request.Latitude, request.Longitude, request.Accuracy);
                throw new UnauthorizedAccessException("Invalid email or password.");
            }
        }

        user.LastLoginAt = DateTime.UtcNow;
        // Single-session enforcement (2026-04-25): rotate CurrentSessionId BEFORE
        // minting the new JWT, so the new token's sid claim matches and any prior
        // device's token (with the OLD sid) becomes invalid the next time it's
        // validated.
        user.CurrentSessionId = Guid.NewGuid();
        var refreshToken = GenerateRefreshToken();
        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(GetRefreshTokenExpiryDays());
        await _userManager.UpdateAsync(user);
        // Invalidate the validator's 30s cached sid so the next request reads
        // the rotated value from the DB instead of a stale prior sid.
        _sessionCache?.Remove($"sid:{user.Id}");

        var roles = await _userManager.GetRolesAsync(user);
        var role = roles.FirstOrDefault() ?? string.Empty;
        var (token, expiresAt) = GenerateJwtToken(user, role);

        // Log login with GPS location
        await LogLoginAuditAsync(user, true, request.Latitude, request.Longitude, request.Accuracy);

        return new LoginResponse
        {
            Token = token,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt,
            UserInfo = new UserInfoDto
            {
                Id = user.Id,
                Email = user.Email ?? string.Empty,
                FullName = user.FullName,
                Role = role,
                EmployeeId = user.EmployeeId,
                Permissions = new List<string>()
            }
        };
    }

    public async Task<LoginResponse> RegisterAsync(RegisterRequest request)
    {
        var existing = await _userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
            throw new InvalidOperationException($"A user with email '{request.Email}' already exists.");

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var createResult = await _userManager.CreateAsync(user, request.Password);
        if (!createResult.Succeeded)
        {
            var errors = string.Join("; ", createResult.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"User creation failed: {errors}");
        }

        var roleResult = await _userManager.AddToRoleAsync(user, request.Role);
        if (!roleResult.Succeeded)
        {
            var errors = string.Join("; ", roleResult.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Role assignment failed: {errors}");
        }

        var refreshToken = GenerateRefreshToken();
        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(GetRefreshTokenExpiryDays());
        // Single-session: RegisterAsync also returns a usable token (auto-login
        // after registration), so rotate the sid here too. ApplicationUser ctor
        // already gave us a fresh Guid, but rotating again is harmless and keeps
        // the contract identical to LoginAsync.
        user.CurrentSessionId = Guid.NewGuid();
        await _userManager.UpdateAsync(user);
        _sessionCache?.Remove($"sid:{user.Id}");

        var (token, expiresAt) = GenerateJwtToken(user, request.Role);

        return new LoginResponse
        {
            Token = token,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt,
            UserInfo = new UserInfoDto
            {
                Id = user.Id,
                Email = user.Email ?? string.Empty,
                FullName = user.FullName,
                Role = request.Role,
                EmployeeId = user.EmployeeId,
                Permissions = new List<string>()
            }
        };
    }

    public async Task<LoginResponse> RefreshTokenAsync(string refreshToken)
    {
        var user = _userManager.Users.FirstOrDefault(u =>
            u.RefreshToken == refreshToken &&
            u.RefreshTokenExpiryTime > DateTime.UtcNow)
            ?? throw new UnauthorizedAccessException("Invalid or expired refresh token.");

        if (!user.IsActive)
            throw new UnauthorizedAccessException("Account is inactive.");

        var newRefreshToken = GenerateRefreshToken();
        user.RefreshToken = newRefreshToken;
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(GetRefreshTokenExpiryDays());
        await _userManager.UpdateAsync(user);

        var roles = await _userManager.GetRolesAsync(user);
        var role = roles.FirstOrDefault() ?? string.Empty;
        var (token, expiresAt) = GenerateJwtToken(user, role);

        return new LoginResponse
        {
            Token = token,
            RefreshToken = newRefreshToken,
            ExpiresAt = expiresAt,
            UserInfo = new UserInfoDto
            {
                Id = user.Id,
                Email = user.Email ?? string.Empty,
                FullName = user.FullName,
                Role = role,
                EmployeeId = user.EmployeeId,
                Permissions = new List<string>()
            }
        };
    }

    public async Task ChangePasswordAsync(int userId, ChangePasswordRequest request)
    {
        // userId here is an application-level int; look up by EmployeeId linkage or treat as string id context
        var user = _userManager.Users.FirstOrDefault(u => u.EmployeeId == userId)
            ?? throw new KeyNotFoundException($"User with EmployeeId {userId} not found.");

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Password change failed: {errors}");
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private (string token, DateTime expiresAt) GenerateJwtToken(ApplicationUser user, string role)
    {
        var jwtSection = _configuration.GetSection("Jwt");
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(jwtSection["Key"] ?? throw new InvalidOperationException("JWT Key is not configured.")));

        var expiryMinutes = int.TryParse(jwtSection["ExpiryMinutes"], out var mins) ? mins : 60;
        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Role, role),
            new("fullName", user.FullName),
            // Single-session enforcement (2026-04-25): sid is rotated by
            // LoginAsync/RegisterAsync; SingleSessionValidator rejects tokens
            // whose sid no longer matches ApplicationUser.CurrentSessionId.
            // RefreshTokenAsync intentionally re-mints with the SAME sid so a
            // refresh doesn't kick other devices — only a fresh Login does.
            new("sid", user.CurrentSessionId.ToString())
        };

        if (user.EmployeeId.HasValue)
            claims.Add(new Claim("employeeId", user.EmployeeId.Value.ToString()));

        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var securityToken = new JwtSecurityToken(
            issuer: jwtSection["Issuer"],
            audience: jwtSection["Audience"],
            claims: claims,
            expires: expiresAt,
            signingCredentials: credentials);

        var handler = new JwtSecurityTokenHandler();
        return (handler.WriteToken(securityToken), expiresAt);
    }

    private static string GenerateRefreshToken()
    {
        var bytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private int GetRefreshTokenExpiryDays()
    {
        var val = _configuration["Jwt:RefreshTokenExpiryDays"];
        return int.TryParse(val, out var days) ? days : 7;
    }

    /// <summary>
    /// Log every login attempt with GPS location and geo-fence check.
    /// </summary>
    private async Task LogLoginAuditAsync(ApplicationUser user, bool success,
        double? latitude, double? longitude, double? accuracy)
    {
        var audit = new LoginAudit
        {
            Email = user.Email ?? "",
            EmployeeId = user.EmployeeId,
            EmployeeName = user.FullName,
            Success = success,
            LoginTime = DateTime.UtcNow,
            Latitude = latitude,
            Longitude = longitude,
            Accuracy = accuracy
        };

        // Check geo-fence against all active locations
        if (latitude.HasValue && longitude.HasValue)
        {
            var locations = await _dbContext.Locations
                .Where(l => l.IsActive && !l.IsDeleted && l.Latitude.HasValue && l.Longitude.HasValue)
                .ToListAsync();

            double nearestDistance = double.MaxValue;
            string? nearestName = null;

            foreach (var loc in locations)
            {
                var dist = HaversineDistanceMeters(
                    latitude.Value, longitude.Value,
                    loc.Latitude!.Value, loc.Longitude!.Value);

                if (dist < nearestDistance)
                {
                    nearestDistance = dist;
                    nearestName = loc.Name;
                    audit.WithinGeoFence = dist <= loc.GeoFenceRadiusMeters;
                }
            }

            audit.NearestLocationName = nearestName;
            audit.DistanceFromNearestLocation = Math.Round(nearestDistance, 1);
        }

        _dbContext.LoginAudits.Add(audit);
        await _dbContext.SaveChangesAsync();
    }

    private static double HaversineDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000; // Earth's radius in meters
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    /// <summary>
    /// Auto-provisions a NickHR ApplicationUser from central auth data.
    /// Used when a user exists in NSCIS but not yet in NickHR.
    /// Creates user with a random local password (never used since central auth is primary).
    /// Default role is "Employee" — admins assign higher roles manually.
    /// </summary>
    private async Task<ApplicationUser?> AutoProvisionUserAsync(CentralAuthResult central)
    {
        var nameParts = (central.FullName ?? string.Empty).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var firstName = nameParts.Length > 0 ? nameParts[0] : central.Username;
        var lastName = nameParts.Length > 1 ? nameParts[1] : string.Empty;

        var user = new ApplicationUser
        {
            UserName = string.IsNullOrEmpty(central.Email) ? central.Username : central.Email,
            Email = central.Email,
            EmailConfirmed = true,
            FirstName = firstName,
            LastName = lastName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            MustChangePassword = false // Password is managed by NSCIS
        };

        // Random password that will never be used — central auth is the only path now
        var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "Aa1!";
        var createResult = await _userManager.CreateAsync(user, randomPassword);

        if (!createResult.Succeeded)
        {
            _logger.LogError("Auto-provision failed for {Email}: {Errors}",
                central.Email, string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return null;
        }

        // Assign default Employee role if it exists
        if (await _userManager.IsInRoleAsync(user, "Employee") == false)
        {
            try
            {
                await _userManager.AddToRoleAsync(user, "Employee");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not assign default Employee role to {Email}", central.Email);
            }
        }

        return user;
    }
}
