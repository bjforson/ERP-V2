using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NickERP.Platform.Identity;
using NickFinance.WebApp.Services;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Resolves a <see cref="PermissionRequirement"/> against the current
/// user's permission bundle from <see cref="IPermissionService"/>.
/// Mirror of NSCIM's <c>PermissionAuthorizationHandler</c>; one of the
/// three components (this + <see cref="DynamicAuthorizationPolicyProvider"/>
/// + <see cref="IPermissionService"/>) that replaces the old role-list
/// <see cref="RoleAuthorizationHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// On deny the handler records a <see cref="SecurityAuditAction.AuthorizationDenied"/>
/// event so the audit log shows which permission a user was missing
/// when they hit a 403. The grant path is intentionally NOT audited —
/// every successful page render would otherwise spam the audit table.
/// </para>
/// </remarks>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IServiceProvider _sp;
    private readonly IHttpContextAccessor _http;
    private readonly ISecurityAuditService _audit;
    private readonly ILogger<PermissionAuthorizationHandler> _log;

    public PermissionAuthorizationHandler(
        IServiceProvider sp,
        IHttpContextAccessor http,
        ISecurityAuditService audit,
        ILogger<PermissionAuthorizationHandler> log)
    {
        _sp = sp;
        _http = http;
        _audit = audit;
        _log = log;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        // Short-circuit: an unauthenticated request can never satisfy a
        // permission requirement. Don't even try to resolve CurrentUser
        // — just fail and let the cookie/CF-Access challenge run.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var current = _sp.GetService<CurrentUser>();
        if (current is null)
        {
            // CurrentUser scope hasn't initialised yet — usually means the
            // request is in a pre-auth window (e.g. login redirect chain).
            // Fail-closed; the next request after auth completes will have
            // CurrentUser available.
            return;
        }

        var permissionService = _sp.GetService<IPermissionService>();
        if (permissionService is null)
        {
            _log.LogError(
                "[AUTH] IPermissionService is not registered; denying every permission policy. Add Program.cs registration.");
            return;
        }

        var hasPermission = await permissionService.HasPermissionAsync(current.UserId, requirement.PermissionName);
        if (hasPermission)
        {
            context.Succeed(requirement);
            return;
        }

        // Denied — log once per failure for the security audit trail. Best
        // effort; an audit-side exception must not turn a clean 403 into a
        // 500.
        try
        {
            await _audit.RecordAsync(
                action: SecurityAuditAction.AuthorizationDenied,
                targetType: "Permission",
                targetId: requirement.PermissionName,
                result: SecurityAuditResult.Denied,
                details: new { userId = current.UserId, permission = requirement.PermissionName });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AUTH] Audit on authorization-denied path failed (non-fatal).");
        }
    }
}
