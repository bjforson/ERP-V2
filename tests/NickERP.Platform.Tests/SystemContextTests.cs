using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy;
using Npgsql;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 5 / G1-3 — verifies the <c>SetSystemContext()</c> mechanism
/// against a real Postgres cluster under the production-equivalent
/// <c>nscim_app</c> role.
///
/// <para>
/// Three behaviours are exercised:
/// </para>
/// <list type="bullet">
///   <item><description>System context permits NULL-tenant inserts to
///         <c>audit.events</c> (the table opted in via
///         <c>20260429061910_AddSystemContextOptInToEvents</c>).</description></item>
///   <item><description>System context does NOT leak reads on tables that
///         have not opted in (canary: <c>inspection.locations</c>).</description></item>
///   <item><description>Without system context, NULL-tenant inserts to
///         <c>audit.events</c> still fail RLS — the opt-in clause is
///         additive, not subtractive (regression of F1 + G1 #4).</description></item>
/// </list>
///
/// <para>
/// Skipped silently when <c>NICKSCAN_DB_PASSWORD</c> is not set so CI
/// without dev Postgres doesn't choke. Per <c>TESTING.md</c>'s dev
/// convention <c>nscim_app</c>'s password equals
/// <c>NICKSCAN_DB_PASSWORD</c>.
/// </para>
/// </summary>
public sealed class SystemContextTests : IDisposable
{
    private const string PlatformDb = "nickerp_platform";
    private const string InspectionDb = "nickerp_inspection";

    private readonly string? _password;
    private readonly List<string> _smokeIdempotencyKeys = new();

    public SystemContextTests()
    {
        _password = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
    }

    /// <summary>
    /// SetSystemContext() lets <c>nscim_app</c> insert a NULL-tenant row
    /// into <c>audit.events</c> and read it back through the opt-in policy.
    /// </summary>
    [Fact]
    public async Task SystemContext_AllowsNullTenantInsert_ToAuditEvents()
    {
        if (string.IsNullOrEmpty(_password)) return; // see class-level skip note
        await using var ctx = BuildAuditDbContext(PlatformDb, out var tenant);
        tenant.SetSystemContext();
        tenant.IsSystem.Should().BeTrue();
        tenant.IsResolved.Should().BeTrue();
        tenant.TenantId.Should().Be(-1L);

        var idempotencyKey = NewIdempotencyKey();
        // Use EF's OpenConnectionAsync so TenantConnectionInterceptor's
        // ConnectionOpenedAsync fires (raw connection.OpenAsync bypasses
        // the EF interceptor pipeline).
        await ctx.Database.OpenConnectionAsync();
        try
        {
            var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();

            // Connection-open fires TenantConnectionInterceptor → pushes
            // app.tenant_id = '-1' since SetSystemContext() set IsSystem.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO audit.events (\"EventId\", \"OccurredAt\", \"IngestedAt\", "
                    + "\"EventType\", \"EntityType\", \"EntityId\", \"TenantId\", "
                    + "\"IdempotencyKey\", \"Payload\") "
                    + "VALUES (gen_random_uuid(), now(), now(), 'sprint5.test.system_insert', "
                    + "'SystemContextTest', 'system-insert', NULL, @key, '{}'::jsonb);";
                var keyParam = cmd.CreateParameter();
                keyParam.ParameterName = "key";
                keyParam.Value = idempotencyKey;
                cmd.Parameters.Add(keyParam);
                var rows = await cmd.ExecuteNonQueryAsync();
                rows.Should().Be(1, "the system-context opt-in policy admits the NULL-tenant write");
            }

            // Read back through the same connection — system context still
            // active so the SELECT sees the row.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM audit.events WHERE \"IdempotencyKey\" = @key;";
                var keyParam = cmd.CreateParameter();
                keyParam.ParameterName = "key";
                keyParam.Value = idempotencyKey;
                cmd.Parameters.Add(keyParam);
                var count = (long)(await cmd.ExecuteScalarAsync())!;
                count.Should().Be(1, "system context reads see suite-wide events");
            }
        }
        finally
        {
            await ctx.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// SetSystemContext() does NOT broaden RLS on tables that have not
    /// opted in. <c>inspection.locations</c> is the canary: its policy has
    /// no <c>OR ... = '-1'</c> clause, so the sentinel does not match any
    /// per-row <c>"TenantId"</c> and reads return zero rows.
    /// </summary>
    [Fact]
    public async Task SystemContext_DoesNotLeakReads_ForNonOptedInTables()
    {
        if (string.IsNullOrEmpty(_password)) return;
        // Ensure the inspection DB has at least one location row to make
        // the assertion non-vacuous: a tenant 1 query under SetTenant(1)
        // should see > 0; system context should see 0.
        var seedKey = await EnsureCanaryLocationAsync();

        await using var conn = new NpgsqlConnection(BuildAppConnectionString(InspectionDb));
        await conn.OpenAsync();

        // Mimic the connection interceptor's behaviour for system mode.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SET app.tenant_id = '-1';";
            await cmd.ExecuteNonQueryAsync();
        }

        long systemCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM inspection.locations;";
            systemCount = (long)(await cmd.ExecuteScalarAsync())!;
        }

        systemCount.Should().Be(0,
            "inspection.locations has not opted in to the system-context sentinel; "
            + "the sentinel '-1' does not match any per-row TenantId so all rows are filtered.");

        // Sanity check: tenant 1 sees at least the seeded canary row.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SET app.tenant_id = '1';";
            await cmd.ExecuteNonQueryAsync();
        }
        long tenantCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM inspection.locations;";
            tenantCount = (long)(await cmd.ExecuteScalarAsync())!;
        }
        tenantCount.Should().BeGreaterThan(0,
            "tenant 1's regular RLS scope sees its own rows — guards against the "
            + "leak-test passing because the table is empty rather than because RLS bites.");

        // Cleanup seeded canary if we created it.
        if (seedKey is not null)
        {
            await DeleteCanaryLocationAsync(seedKey);
        }
    }

    /// <summary>
    /// Without SetSystemContext(), regular tenant 1 cannot insert a
    /// NULL-tenant row to <c>audit.events</c>. The opt-in clause is
    /// additive — it admits the system sentinel ONLY; regular tenants
    /// still fail the WITH CHECK on <c>"TenantId" = NULL</c>. This is the
    /// regression assertion for the F1 + G1 #4 invariant.
    /// </summary>
    [Fact]
    public async Task WithoutSystemContext_RejectsNullTenantInsert_ToAuditEvents()
    {
        if (string.IsNullOrEmpty(_password)) return;
        await using var ctx = BuildAuditDbContext(PlatformDb, out var tenant);
        tenant.SetTenant(1L);
        tenant.IsSystem.Should().BeFalse();
        tenant.TenantId.Should().Be(1L);

        var idempotencyKey = NewIdempotencyKey();
        // Use EF's OpenConnectionAsync so TenantConnectionInterceptor
        // fires and pushes app.tenant_id = '1' (regular tenant scope).
        await ctx.Database.OpenConnectionAsync();
        try
        {
            var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO audit.events (\"EventId\", \"OccurredAt\", \"IngestedAt\", "
                + "\"EventType\", \"EntityType\", \"EntityId\", \"TenantId\", "
                + "\"IdempotencyKey\", \"Payload\") "
                + "VALUES (gen_random_uuid(), now(), now(), 'sprint5.test.regression', "
                + "'SystemContextTest', 'regression-insert', NULL, @key, '{}'::jsonb);";
            var keyParam = cmd.CreateParameter();
            keyParam.ParameterName = "key";
            keyParam.Value = idempotencyKey;
            cmd.Parameters.Add(keyParam);

            var act = async () => await cmd.ExecuteNonQueryAsync();
            // Postgres surfaces RLS rejection as PostgresException with
            // SqlState 42501 (insufficient_privilege) and message
            // "new row violates row-level security policy".
            var ex = await act.Should().ThrowAsync<PostgresException>();
            ex.Which.Message.Should().Contain("row-level security",
                "the WITH CHECK clause must reject NULL-tenant inserts outside system context");
        }
        finally
        {
            await ctx.Database.CloseConnectionAsync();
        }
    }

    private AuditDbContext BuildAuditDbContext(string database, out TenantContext tenant)
    {
        var ctxTenant = new TenantContext();
        tenant = ctxTenant;
        var connectionString = BuildAppConnectionString(database);
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(
                new TenantConnectionInterceptor(ctxTenant, NullLogger<TenantConnectionInterceptor>.Instance),
                new TenantOwnedEntityInterceptor(ctxTenant))
            .Options;
        return new AuditDbContext(options);
    }

    private string BuildAppConnectionString(string database)
        => $"Host=localhost;Port=5432;Database={database};Username=nscim_app;Password={_password};Pooling=false";

    private string BuildAdminConnectionString(string database)
    {
        // Per H3 / FU-5 — postgres uses the same dev password.
        return $"Host=localhost;Port=5432;Database={database};Username=postgres;Password={_password};Pooling=false";
    }

    private string NewIdempotencyKey()
    {
        var key = $"sprint5-systemcontext-{Guid.NewGuid():N}";
        _smokeIdempotencyKeys.Add(key);
        return key;
    }

    /// <summary>
    /// Inserts a tenant-1 location if the canary row for this test class
    /// is missing, returning the seeded code to delete on tear-down.
    /// Returns <c>null</c> if a tenant-1 location already exists (we don't
    /// own it; don't delete).
    /// </summary>
    private async Task<string?> EnsureCanaryLocationAsync()
    {
        await using var conn = new NpgsqlConnection(BuildAdminConnectionString(InspectionDb));
        await conn.OpenAsync();

        // Bypass RLS via postgres role to check tenant 1's count irrespective
        // of policy.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM inspection.locations WHERE \"TenantId\" = 1;";
            var cnt = (long)(await cmd.ExecuteScalarAsync())!;
            if (cnt > 0)
            {
                return null;
            }
        }

        var canaryCode = $"sprint5-canary-{Guid.NewGuid():N}".Substring(0, 32);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = GetInsertCanaryLocationSql();
            var codeParam = cmd.CreateParameter();
            codeParam.ParameterName = "code";
            codeParam.Value = canaryCode;
            cmd.Parameters.Add(codeParam);
            await cmd.ExecuteNonQueryAsync();
        }
        return canaryCode;
    }

    private async Task DeleteCanaryLocationAsync(string code)
    {
        await using var conn = new NpgsqlConnection(BuildAdminConnectionString(InspectionDb));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM inspection.locations WHERE \"Code\" = @code AND \"TenantId\" = 1;";
        var p = cmd.CreateParameter();
        p.ParameterName = "code";
        p.Value = code;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string GetInsertCanaryLocationSql()
    {
        // The exact column set comes from inspection.locations; we set the
        // minimum required NOT-NULL columns (TimeZone has no default) +
        // reasonable values. Discovered empirically; if a future
        // inspection-module migration adds NOT NULL columns this test
        // seeder will need updating.
        return @"INSERT INTO inspection.locations
            (""Id"", ""TenantId"", ""Code"", ""Name"", ""TimeZone"", ""IsActive"", ""CreatedAt"")
            VALUES (gen_random_uuid(), 1, @code, 'Sprint5 Canary', 'Africa/Accra', true, now())
            ON CONFLICT (""TenantId"", ""Code"") DO NOTHING;";
    }

    public void Dispose()
    {
        if (_password is null) return;
        // Best-effort cleanup of any audit rows left over from system-context
        // tests. Deletes via postgres because nscim_app lacks DELETE on
        // audit.events (append-only role grant).
        try
        {
            using var conn = new NpgsqlConnection(BuildAdminConnectionString(PlatformDb));
            conn.Open();
            foreach (var key in _smokeIdempotencyKeys)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM audit.events WHERE \"IdempotencyKey\" = @key;";
                var p = cmd.CreateParameter();
                p.ParameterName = "key";
                p.Value = key;
                cmd.Parameters.Add(p);
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // best-effort
        }
    }
}

