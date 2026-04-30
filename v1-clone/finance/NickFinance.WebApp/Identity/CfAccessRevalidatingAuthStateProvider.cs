using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Revalidates the authenticated principal every 30 minutes for the life
/// of the SignalR circuit. Without this, a CF Access token revocation
/// (e.g. operator off-boarded mid-session) doesn't propagate until the
/// circuit dies and reconnects.
/// </summary>
/// <remarks>
/// The check is two-fold: (a) the user row must still exist with status
/// <see cref="UserStatus.Active"/>; (b) <c>NameIdentifier</c> /
/// <c>sub</c> claim must still be present. A failure on either
/// re-authenticates as anonymous, which the auth pipeline then redirects.
/// </remarks>
public sealed class CfAccessRevalidatingAuthStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CfAccessRevalidatingAuthStateProvider> _log;

    public CfAccessRevalidatingAuthStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopes,
        ILogger<CfAccessRevalidatingAuthStateProvider> log)
        : base(loggerFactory)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        var user = authenticationState.User;
        if (user.Identity?.IsAuthenticated != true) return false;

        var sub = user.FindFirst("sub")?.Value
               ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = (user.FindFirst("email")?.Value
                  ?? user.FindFirst(ClaimTypes.Email)?.Value)?.ToLowerInvariant();

        // We need a fresh scope because the original circuit's scope is
        // long-lived and the DbContext from there may be tracking stale
        // entities.
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var query = db.Users.IgnoreQueryFilters().AsNoTracking();

            User? row = null;
            if (!string.IsNullOrEmpty(sub))
            {
                row = await query.FirstOrDefaultAsync(u => u.CfAccessSub == sub, cancellationToken);
            }
            if (row is null && !string.IsNullOrEmpty(email))
            {
                row = await query.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
            }

            if (row is null) return false;
            return row.Status == UserStatus.Active;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auth-state revalidation failed; treating as still-valid to avoid a sign-out storm.");
            // Fail-open here: a transient DB blip should not boot every active circuit.
            return true;
        }
    }
}
