using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// Sprint 9 / FU-userid — reads <c>nickerp:id</c> from the authenticated
/// principal and stamps it onto the per-request <see cref="IUserContext"/>.
/// Mirrors <see cref="TenantResolutionMiddleware"/>; place AFTER
/// <c>UseAuthentication()</c> + <c>UseAuthorization()</c> in the pipeline.
/// </summary>
/// <remarks>
/// Reads the claim type as a string constant rather than referencing
/// <c>NickERP.Platform.Identity.Auth.NickErpClaims</c> directly — the
/// Tenancy layer must not depend on Identity. Apps that adopt a different
/// claim name can swap the middleware.
///
/// On unauthenticated requests / missing claim, the middleware leaves
/// <see cref="IUserContext.IsResolved"/> as <c>false</c>; the
/// <see cref="TenantConnectionInterceptor"/> then pushes the zero-UUID
/// fail-closed default to Postgres. User-scoped RLS policies match nothing.
/// </remarks>
public sealed class UserResolutionMiddleware
{
    /// <summary>The claim type the Identity layer puts the user id on.</summary>
    public const string UserIdClaimType = "nickerp:id";

    private readonly RequestDelegate _next;
    private readonly ILogger<UserResolutionMiddleware> _logger;

    public UserResolutionMiddleware(RequestDelegate next, ILogger<UserResolutionMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext ctx, IUserContext user)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(user);

        var claim = ctx.User?.FindFirst(UserIdClaimType)?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var id) && id != Guid.Empty)
        {
            user.SetUser(id);
        }
        else if (ctx.User?.Identity?.IsAuthenticated == true)
        {
            // Authenticated principal with no/invalid user id claim — log so it's visible in Seq.
            _logger.LogWarning(
                "Authenticated principal has no valid '{Claim}' claim; user context left unresolved. Path={Path}",
                UserIdClaimType, ctx.Request.Path);
        }

        await _next(ctx);
    }
}
