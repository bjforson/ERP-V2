using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NickHR.Infrastructure.Data;

namespace NickHR.WebApp.Services;

/// <summary>
/// Resolves the current logged-in user's Employee ID.
/// Works with both cookie auth (Identity) and JWT auth.
/// </summary>
public class CurrentEmployeeService
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IServiceProvider _serviceProvider;
    private int? _cachedEmployeeId;
    private bool _resolved;

    public CurrentEmployeeService(
        AuthenticationStateProvider authStateProvider,
        IServiceProvider serviceProvider)
    {
        _authStateProvider = authStateProvider;
        _serviceProvider = serviceProvider;
    }

    public async Task<int?> GetEmployeeIdAsync()
    {
        if (_resolved) return _cachedEmployeeId;

        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            _resolved = true;
            return null;
        }

        // 1. Check JWT employeeId claim
        var empIdClaim = user.FindFirst("employeeId")?.Value;
        if (int.TryParse(empIdClaim, out var empId))
        {
            _cachedEmployeeId = empId;
            _resolved = true;
            return empId;
        }

        // 2. Get email/username from claims (works for both cookie and JWT)
        var email = user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                    ?? user.FindFirst("email")?.Value
                    ?? user.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                    ?? user.Identity?.Name;

        if (string.IsNullOrEmpty(email))
        {
            _resolved = true;
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NickHRDbContext>();

        // 3. Look up ApplicationUser to get EmployeeId
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var appUser = await userManager.FindByEmailAsync(email)
                      ?? await userManager.FindByNameAsync(email);

        if (appUser?.EmployeeId != null)
        {
            _cachedEmployeeId = appUser.EmployeeId;
            _resolved = true;
            return appUser.EmployeeId;
        }

        // 4. Fallback: look up Employee by email
        var emp = await db.Employees.FirstOrDefaultAsync(e =>
            e.WorkEmail == email && !e.IsDeleted);

        _cachedEmployeeId = emp?.Id;
        _resolved = true;
        return emp?.Id;
    }
}
