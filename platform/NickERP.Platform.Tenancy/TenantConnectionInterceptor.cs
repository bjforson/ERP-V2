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

    private long ResolvedId() => _tenant.IsResolved ? _tenant.TenantId : 0L;
}
