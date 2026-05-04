using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// EF Core <see cref="DbConnectionInterceptor"/> that pushes the current
/// <see cref="ITenantContext.TenantId"/> down to the Postgres session via
/// <c>set_config('app.tenant_id', $1, false)</c> every time a connection is
/// opened (or pulled from the pool).
///
/// This is what makes the existing tenant_isolation_* row-level-security
/// policies actually enforce something. Before this interceptor existed, the
/// policy expression
/// <code>tenant_id = COALESCE(NULLIF(current_setting('app.tenant_id', true), ''), '1')::bigint</code>
/// would always read the empty/unset case and fall through to the literal
/// '1' default, meaning RLS was a silent no-op. Wiring this in turns the
/// dormant policy into an active filter; once the COALESCE default is later
/// switched to '0' (fail-closed) the policy will refuse rows on any session
/// that bypasses this interceptor.
///
/// Pooling note: <see cref="set_config"/> with <c>is_local=false</c> binds
/// the value to the SESSION, not the transaction. That's deliberate — EF
/// Core can run multiple commands across a single open connection without
/// a wrapping transaction, and every one of them needs the tenant set.
/// Returning the connection to the pool resets <c>app.tenant_id</c> via
/// Npgsql's <c>RESET ALL</c> on close, so the next pool consumer always
/// re-sets it through this interceptor before its first query runs.
///
/// Registered as scoped — <see cref="ITenantContext"/> is per-request scope
/// and the interceptor must read the request's tenant id, not a stale value.
/// </summary>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    private const string SettingName = "app.tenant_id";

    private readonly ITenantContext _tenantContext;
    private readonly ILogger<TenantConnectionInterceptor> _logger;

    public TenantConnectionInterceptor(
        ITenantContext tenantContext,
        ILogger<TenantConnectionInterceptor> logger)
    {
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        ApplyTenantSetting(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyTenantSettingAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyTenantSetting(DbConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            ConfigureCommand(cmd);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to set app.tenant_id={TenantId} on opened connection — RLS will fall back to the COALESCE default.",
                _tenantContext.TenantId);
        }
    }

    private async Task ApplyTenantSettingAsync(DbConnection connection, CancellationToken ct)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            ConfigureCommand(cmd);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to set app.tenant_id={TenantId} on opened connection — RLS will fall back to the COALESCE default.",
                _tenantContext.TenantId);
        }
    }

    private void ConfigureCommand(DbCommand cmd)
    {
        // set_config(name, value, is_local) — we use is_local=false so the
        // value persists across statements on the same pooled connection.
        // Both arguments are passed as parameters, which Npgsql handles as
        // proper bind parameters; this avoids any string concatenation
        // attack surface even though the tenant id is an internal long.
        cmd.CommandText = "SELECT set_config(@name, @value, false)";

        var nameParam = cmd.CreateParameter();
        nameParam.ParameterName = "@name";
        nameParam.DbType = DbType.String;
        nameParam.Value = SettingName;
        cmd.Parameters.Add(nameParam);

        var valueParam = cmd.CreateParameter();
        valueParam.ParameterName = "@value";
        valueParam.DbType = DbType.String;
        valueParam.Value = _tenantContext.TenantId.ToString();
        cmd.Parameters.Add(valueParam);
    }
}
