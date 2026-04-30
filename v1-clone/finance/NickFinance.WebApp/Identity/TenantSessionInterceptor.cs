using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NickERP.Platform.Identity;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// EF Core <see cref="DbCommandInterceptor"/> that prepends a
/// <c>SET nickerp.current_tenant_id = '{id}'</c> statement to every
/// outgoing command. The companion Postgres RLS policy applied by
/// <c>scripts/apply-rls-policies.sql</c> uses
/// <c>current_setting('nickerp.current_tenant_id', true)</c> to scope
/// every business table to the authenticated tenant.
/// </summary>
/// <remarks>
/// <para>
/// This is the *defence-in-depth* layer behind the EF query filters.
/// Even if a future code path constructs a raw SQL query, or somebody
/// adds a DbContext that forgets the <c>HasQueryFilter</c> wiring, the
/// database itself drops cross-tenant rows.
/// </para>
/// <para>
/// We use plain <c>SET</c> (session-scoped) rather than <c>SET LOCAL</c>
/// (transaction-scoped) so it sticks for ad-hoc queries that aren't
/// wrapped in an explicit transaction. With Npgsql connection pooling
/// the pool resets the GUC on connection return (`ResetSession`), so the
/// scope leak is bounded to the request that issued it.
/// </para>
/// <para>
/// When <see cref="ITenantAccessor.Current"/> returns null (bootstrap
/// CLI, smoke test, system jobs), the interceptor is a no-op and the
/// RLS policy permits all rows — same semantics as the EF query filters.
/// </para>
/// <para>
/// Because <see cref="ITenantAccessor"/> is registered scoped and this
/// interceptor is constructed per-DbContext via
/// <c>AddDbContext((sp, opts) =&gt; opts.AddInterceptors(...))</c>, each
/// request gets a fresh interceptor with a fresh accessor — no caching
/// hazard.
/// </para>
/// </remarks>
public sealed class TenantSessionInterceptor : DbCommandInterceptor
{
    private readonly ITenantAccessor _tenant;

    public TenantSessionInterceptor(ITenantAccessor tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        _tenant = tenant;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Apply(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Apply(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Apply(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Apply(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Apply(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Apply(DbCommand command)
    {
        var tenantId = _tenant.Current;
        if (tenantId is null)
        {
            // No tenant context — bootstrap CLI, smoke run, etc. Don't
            // touch the command; RLS policy permits all rows when the
            // GUC is unset.
            return;
        }

        // SET accepts only literals or a SQL expression; we cast the
        // long to text. Casting a bigint via `.ToString(InvariantCulture)`
        // produces digits-only ASCII — safe to interpolate, but we still
        // guard against any future weirdness with a defensive check.
        var idText = tenantId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!IsAllDigitsOrSign(idText))
        {
            // Defensive: refuse to inject anything weird into the SET.
            // This branch should be unreachable for `long`.
            throw new InvalidOperationException($"Refusing to set tenant id with non-numeric text: '{idText}'.");
        }

        // Plain `SET` sticks for the connection-lifetime; Npgsql resets
        // it on connection return. We do NOT use SET LOCAL because some
        // EF queries run outside an explicit transaction.
        command.CommandText = $"SET nickerp.current_tenant_id = '{idText}'; " + command.CommandText;
    }

    private static bool IsAllDigitsOrSign(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i == 0 && (c == '-' || c == '+')) continue;
            if (c < '0' || c > '9') return false;
        }
        return true;
    }
}
