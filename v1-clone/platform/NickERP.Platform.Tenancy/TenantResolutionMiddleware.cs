using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// ASP.NET Core middleware that populates <see cref="ITenantContext"/>
/// from the JWT <c>tenant_id</c> claim on every authenticated request.
///
/// Order: must run AFTER <c>UseAuthentication()</c> but BEFORE any
/// controller/endpoint that touches tenant-owned data.
///
/// Behavior:
/// - Authenticated request with valid <c>tenant_id</c> claim → resolves tenant
/// - Authenticated request WITHOUT a tenant claim → falls back to default tenant 1
///   (this happens for legacy tokens during the migration window — log a warning)
/// - Anonymous request → leaves the context unresolved; downstream code that
///   requires a tenant will throw
/// - Platform admins (claim <c>platform_admin=true</c>) can impersonate via
///   the <c>X-NickERP-Tenant</c> header
/// </summary>
public sealed class TenantResolutionMiddleware
{
    public const string ImpersonationHeader = "X-NickERP-Tenant";
    public const string TenantClaimType = "tenant_id";
    public const string PlatformAdminClaimType = "platform_admin";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            // Platform admin flag from JWT
            if (tenantContext is TenantContext concrete)
            {
                concrete.IsPlatformAdmin = string.Equals(
                    context.User.FindFirst(PlatformAdminClaimType)?.Value,
                    "true",
                    StringComparison.OrdinalIgnoreCase);
            }

            // Resolve tenant from claim
            var tenantClaim = context.User.FindFirst(TenantClaimType)?.Value;
            if (long.TryParse(tenantClaim, out var tenantId) && tenantId > 0)
            {
                tenantContext.SetTenant(tenantId);
            }
            else
            {
                // Legacy token without tenant claim — log once per session-ish window
                _logger.LogWarning(
                    "Authenticated request without tenant_id claim from user {User} on {Path}. " +
                    "Falling back to default tenant. Issue a fresh token to fix.",
                    context.User.Identity.Name, context.Request.Path);
                tenantContext.SetTenant(Tenant.DefaultTenantId);
            }

            // Optional impersonation by platform admins
            if (tenantContext.IsPlatformAdmin && context.Request.Headers.TryGetValue(ImpersonationHeader, out var impersonatedTenant))
            {
                if (long.TryParse(impersonatedTenant, out var impersonatedId) && impersonatedId > 0)
                {
                    _logger.LogInformation(
                        "Platform admin {User} impersonating tenant {TenantId} on {Path}",
                        context.User.Identity.Name, impersonatedId, context.Request.Path);
                    tenantContext.SetTenant(impersonatedId);
                }
            }
        }

        await _next(context);
    }
}
