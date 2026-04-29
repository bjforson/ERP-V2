using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// EF Core <see cref="IDbConnectionInterceptor"/> that pushes the current
/// tenant id and user id down to Postgres on every connection open via
/// <c>SET app.tenant_id</c> + <c>SET app.user_id</c>. Postgres Row-Level
/// Security policies on every business table read these session variables
/// to filter rows — even if module code forgets a WHERE clause or query
/// filter, RLS catches it.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>SET</c> not <c>SET LOCAL</c> because EF Core's connection lifetime
/// can outlive a single transaction (pooled connections). Both session
/// variables are reset on every <see cref="ConnectionOpenedAsync"/>
/// invocation — which fires once per pool checkout — guaranteeing fresh
/// scope per request.
/// </para>
/// <para>
/// If the tenant context is not resolved (anonymous request, background
/// job that hasn't impersonated yet), <c>app.tenant_id</c> is set to
/// <c>'0'</c> so RLS policies fail closed by default. Make sure RLS policy
/// definitions COALESCE to <c>'0'</c> too (see v1's
/// reference_rls_now_enforces.md).
/// </para>
/// <para>
/// When <see cref="ITenantContext.IsSystem"/> is <c>true</c> (i.e. the
/// caller invoked <see cref="ITenantContext.SetSystemContext"/>), the
/// variable is set to the sentinel <c>'-1'</c>. RLS policies that opt
/// in to system access via <c>OR current_setting('app.tenant_id') = '-1'</c>
/// will allow cross-tenant reads / NULL-tenant writes; tables that have
/// not opted in continue to filter on the per-row <c>"TenantId"</c>
/// (which never equals the sentinel), so reads return zero rows and
/// writes fail the policy's WITH CHECK clause. Sprint 5 (G1-3) is the
/// first table to opt in (<c>audit.events</c>); see
/// <c>docs/system-context-audit-register.md</c>.
/// </para>
/// <para>
/// Sprint 9 / FU-userid — also pushes <c>app.user_id</c> from
/// <see cref="IUserContext"/>. Mirrors the tenant fail-closed pattern:
/// when no current user is resolved (background workers, anonymous
/// requests), pushes the zero UUID
/// <c>'00000000-0000-0000-0000-000000000000'</c>. User-scoped RLS
/// policies (e.g. <c>tenant_user_isolation_notifications</c> on
/// <c>audit.notifications</c>) compare <c>"UserId"</c> against this
/// session value, so the zero UUID matches nothing — fail closed.
/// </para>
/// <para>
/// Choice of zero UUID for system context (FU-userid Phase A step 3):
/// when <see cref="ITenantContext.IsSystem"/> is <c>true</c>, we deliberately
/// push the same zero UUID as for "no user" rather than introducing a
/// distinct system-actor sentinel. Reason — <c>audit.notifications</c>
/// is per-user; the system-context projector that reads <c>audit.events</c>
/// has no business reading anyone's notifications, so the zero UUID
/// matching no user is the safe default. The projector inserts via the
/// system-context OR clause, not by matching a specific UserId.
/// </para>
/// </remarks>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// Fail-closed default for <c>app.user_id</c> — matches no real user,
    /// so user-scoped RLS policies filter everything out.
    /// </summary>
    public const string UserIdDefault = "00000000-0000-0000-0000-000000000000";

    private readonly ITenantContext _tenant;
    private readonly IUserContext _user;
    private readonly ILogger<TenantConnectionInterceptor> _logger;

    public TenantConnectionInterceptor(
        ITenantContext tenant,
        IUserContext user,
        ILogger<TenantConnectionInterceptor> logger)
    {
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SET app.tenant_id = '{ResolvedTenantId()}'; "
                + $"SET app.user_id = '{ResolvedUserId()}';";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push tenant id / user id to Postgres on ConnectionOpened");
        }
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SET app.tenant_id = '{ResolvedTenantId()}'; "
                + $"SET app.user_id = '{ResolvedUserId()}';";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push tenant id / user id to Postgres on ConnectionOpenedAsync");
        }
    }

    private long ResolvedTenantId()
    {
        if (_tenant.IsSystem)
        {
            // Sprint 5 (G1-3) — system-context sentinel. Tables that opt in
            // see this and allow cross-tenant access; non-opt-in tables
            // filter to zero rows.
            return TenantContext.SystemSentinel;
        }
        return _tenant.IsResolved ? _tenant.TenantId : 0L;
    }

    private string ResolvedUserId()
    {
        // FU-userid — system context resolves to the zero UUID (no user),
        // matching the comment block above. The system context's writes
        // pass user-scoped RLS via the OR-clause on `app.tenant_id = '-1'`,
        // not by matching a real UserId.
        if (_tenant.IsSystem || !_user.IsResolved)
        {
            return UserIdDefault;
        }
        return _user.UserId.ToString();
    }
}
