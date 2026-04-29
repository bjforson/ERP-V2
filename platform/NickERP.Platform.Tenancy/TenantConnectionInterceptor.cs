using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// EF Core <see cref="IDbConnectionInterceptor"/> that pushes the current
/// tenant id down to Postgres on every connection open via
/// <c>SET LOCAL app.tenant_id</c>. Postgres Row-Level Security policies on
/// every business table read this session variable to filter rows — even
/// if module code forgets a WHERE clause or query filter, RLS catches it.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>SET</c> not <c>SET LOCAL</c> because EF Core's connection lifetime
/// can outlive a single transaction (pooled connections). The <c>app.tenant_id</c>
/// session variable is reset on every <see cref="ConnectionOpenedAsync"/>
/// invocation — which fires once per pool checkout — guaranteeing fresh
/// tenant scope per request.
/// </para>
/// <para>
/// If the tenant context is not resolved (anonymous request, background
/// job that hasn't impersonated yet), the variable is set to <c>'0'</c>
/// so RLS policies fail closed by default. Make sure RLS policy definitions
/// COALESCE to <c>'0'</c> too (see v1's reference_rls_now_enforces.md).
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
/// </remarks>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext _tenant;
    private readonly ILogger<TenantConnectionInterceptor> _logger;

    public TenantConnectionInterceptor(ITenantContext tenant, ILogger<TenantConnectionInterceptor> logger)
    {
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SET app.tenant_id = '{ResolvedId()}';";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push tenant id to Postgres on ConnectionOpened");
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
            cmd.CommandText = $"SET app.tenant_id = '{ResolvedId()}';";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push tenant id to Postgres on ConnectionOpenedAsync");
        }
    }

    private long ResolvedId()
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
}
