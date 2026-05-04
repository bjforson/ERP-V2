using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NickHR.Core.Interfaces;

namespace NickHR.Services.Auth;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    public string UserId =>
        Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    public string UserName =>
        Principal?.FindFirstValue(ClaimTypes.Email) ?? string.Empty;

    public int? EmployeeId
    {
        get
        {
            var raw = Principal?.FindFirstValue("employeeId");
            return int.TryParse(raw, out var id) ? id : null;
        }
    }

    public string Role =>
        Principal?.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

    public bool IsAuthenticated =>
        Principal?.Identity?.IsAuthenticated ?? false;

    public Task<bool> CanAccessEmployeeAsync(int employeeId, params string[] privilegedRoles)
    {
        // Self-access is always allowed.
        if (EmployeeId is int self && self == employeeId)
            return Task.FromResult(true);

        // Privileged roles bypass the self check (HR/SuperAdmin/PayrollAdmin etc).
        var principal = Principal;
        if (principal != null && privilegedRoles is { Length: > 0 })
        {
            foreach (var role in privilegedRoles)
            {
                if (!string.IsNullOrEmpty(role) && principal.IsInRole(role))
                    return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }
}
