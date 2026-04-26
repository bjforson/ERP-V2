using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// Reads <c>nickerp:tenant_id</c> from the authenticated principal and stamps
/// it onto the per-request <see cref="ITenantContext"/>. Place AFTER
/// <c>UseAuthentication()</c> + <c>UseAuthorization()</c> in the pipeline,
/// BEFORE any module endpoints that touch tenant-owned data.
/// </summary>
/// <remarks>
/// Reads the claim type as a string constant rather than referencing
/// <c>NickERP.Platform.Identity.Auth.NickErpClaims</c> directly — the
/// Tenancy layer must not depend on Identity. Apps that adopt a different
/// claim name (e.g. multi-IdP setups) can swap the middleware.
///
/// On unauthenticated requests, the middleware leaves <see cref="ITenantContext.IsResolved"/>
/// as <c>false</c>; downstream code is expected to authorize before
/// reading tenant data, so a missing tenant produces no rows (the EF query
/// filter sees <c>TenantId == 0</c> and matches nothing).
/// </remarks>
public sealed class TenantResolutionMiddleware
{
    /// <summary>The claim type the Identity layer puts the tenant id on.</summary>
    public const string TenantIdClaimType = "nickerp:tenant_id";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext ctx, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(tenant);

        var claim = ctx.User?.FindFirst(TenantIdClaimType)?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && long.TryParse(claim, out var id) && id > 0)
        {
            tenant.SetTenant(id);
        }
        else if (ctx.User?.Identity?.IsAuthenticated == true)
        {
            // Authenticated principal with no/invalid tenant claim — log so it's visible in Seq.
            _logger.LogWarning(
                "Authenticated principal has no valid '{Claim}' claim; tenant context left unresolved. Path={Path}",
                TenantIdClaimType, ctx.Request.Path);
        }

        await _next(ctx);
    }
}
