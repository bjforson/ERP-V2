using Microsoft.AspNetCore.Identity;

namespace NickHR.Infrastructure.Data;

public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName => $"{FirstName} {LastName}";
    public int? EmployeeId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public bool MustChangePassword { get; set; } = false;
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiryTime { get; set; }

    /// <summary>
    /// Single-session enforcement (added 2026-04-25). Rotated on every successful
    /// LoginAsync / RefreshTokenAsync / RegisterAsync. Embedded as <c>sid</c>
    /// claim in the JWT; <c>JwtBearerEvents.OnTokenValidated</c> rejects tokens
    /// whose sid no longer matches this column.
    /// </summary>
    public Guid CurrentSessionId { get; set; } = Guid.NewGuid();
}
