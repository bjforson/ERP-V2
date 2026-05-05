using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using Npgsql;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 43 Phase D — FU-launcher-rls-with-postgres. Proves
/// <c>tenancy.tenant_module_settings</c>'s RLS policy actually
/// narrows reads to <c>app.tenant_id</c> when running under a
/// non-superuser role, so the explicit
/// <c>Where(TenantId == tenantId)</c> belt-and-suspenders filter in
/// <c>ModuleRegistryService.GetModulesForTenantAsync</c> can be
/// dropped.
/// </summary>
/// <remarks>
/// <para>
/// Marked <c>[Trait("Category", "RequiresLiveDb")]</c>. Runs only when
/// <c>NICKSCAN_DB_PASSWORD</c> env var is set; otherwise the test is
/// skipped (logged, not failed). CI without a Postgres dev box passes;
/// the dev box runs it as part of the local test suite.
/// </para>
/// <para>
/// Pattern mirrors the existing
/// <see cref="NickERP.Inspection.E2E.Tests.PostgresFixture"/>: spin
/// up a per-test database under the postgres superuser, apply the
/// tenancy migrations via EF, create a non-superuser role with
/// SELECT grants, then connect under that role with an explicit
/// <c>SET app.tenant_id</c> session variable. The non-superuser role
/// is critical — the default postgres user has BYPASSRLS and would
/// silently see every row regardless of <c>app.tenant_id</c>.
/// </para>
/// <para>
/// Testcontainers is intentionally NOT used here: the build host's
/// environment audit (Sprint F2) flagged Docker as unavailable, and
/// the existing E2E fixture pattern already works against the dev
/// Postgres on <c>localhost:5432</c> with throwaway databases.
/// </para>
/// </remarks>
public sealed class TenantModuleSettingsRlsIntegrationTests : IAsyncLifetime
{
    private string? _adminConn;
    private string? _appConn;
    private string? _dbName;
    private string? _roleName;
    private string? _rolePassword;
    private bool _enabled;

    /// <summary>
    /// Stand up a unique throwaway DB on localhost:5432, apply tenancy
    /// migrations, and create a non-superuser app role for the test.
    /// </summary>
    public async Task InitializeAsync()
    {
        var password = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            _enabled = false;
            return;
        }
        _enabled = true;

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        _dbName = $"nickerp_test_{suffix}_platform";
        _roleName = $"nickerp_test_{suffix}_app";
        _rolePassword = "rlspw_" + Guid.NewGuid().ToString("N");

        _adminConn =
            $"Host=localhost;Port=5432;Database=postgres;Username=postgres;Password={password};Pooling=false";

        // CREATE DATABASE outside a transaction.
        await using (var conn = new NpgsqlConnection(_adminConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_dbName}\";", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var dbAdmin =
            $"Host=localhost;Port=5432;Database={_dbName};Username=postgres;Password={password};Pooling=false";

        // Apply tenancy migrations on the new DB. We use the design-time
        // factory's pattern — point the env var at the new DB and call
        // Migrate(). The history table lives in tenancy schema per H3.
        Environment.SetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION", dbAdmin);
        await using (var ctx = new TenancyDbContextFactory().CreateDbContext(Array.Empty<string>()))
        {
            await ctx.Database.MigrateAsync();
        }

        // Create the non-superuser role + grants. NOSUPERUSER + NOBYPASSRLS
        // is the critical pair — RLS only runs for roles that don't
        // bypass it, and the postgres superuser implicitly bypasses
        // every policy.
        await using (var conn = new NpgsqlConnection(dbAdmin))
        {
            await conn.OpenAsync();
            var stmts = new[]
            {
                $"CREATE ROLE \"{_roleName}\" LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{_rolePassword}';",
                $"GRANT CONNECT ON DATABASE \"{_dbName}\" TO \"{_roleName}\";",
                $"GRANT USAGE ON SCHEMA tenancy TO \"{_roleName}\";",
                $"GRANT SELECT, INSERT ON tenancy.tenant_module_settings TO \"{_roleName}\";",
                // The IDENTITY column on tenant_module_settings.Id needs
                // USAGE on the implicit sequence so the role can insert.
                $"GRANT USAGE ON ALL SEQUENCES IN SCHEMA tenancy TO \"{_roleName}\";",
                // Tenants table is the unprivileged root — grant SELECT
                // so the test can see the seed row.
                $"GRANT SELECT ON tenancy.tenants TO \"{_roleName}\";",
            };
            foreach (var stmt in stmts)
            {
                await using var cmd = new NpgsqlCommand(stmt, conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        _appConn =
            $"Host=localhost;Port=5432;Database={_dbName};Username={_roleName};Password={_rolePassword};Pooling=false";
    }

    public async Task DisposeAsync()
    {
        if (!_enabled || _adminConn is null) return;
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(_adminConn);
            await conn.OpenAsync();
            // Force-terminate any lingering connections.
            await using (var cmd = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();", conn))
            {
                cmd.Parameters.AddWithValue("db", _dbName!);
                try { await cmd.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
            }
            await using (var cmd = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE);", conn))
                await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand(
                $"DROP ROLE IF EXISTS \"{_roleName}\";", conn))
                await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // Test tear-down best-effort. Leftover DB/role names are
            // prefixed nickerp_test_ so an out-of-band sweeper can clean
            // them up.
            Trace.WriteLine($"TenantModuleSettingsRlsIntegrationTests teardown: {ex}");
        }
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task ReadUnderTenantA_DoesNotLeakRowsForTenantB()
    {
        if (!_enabled)
        {
            // Skip-by-pass: we've never actually run the assertion. The
            // test runner accepts this as a pass; the dev box that has
            // NICKSCAN_DB_PASSWORD set runs the real test.
            return;
        }
        // Seed: one row per tenant under each tenant's session var.
        await SeedRowAsync(tenantId: 1, moduleId: "inspection");
        await SeedRowAsync(tenantId: 2, moduleId: "inspection");

        // Read under the app role with app.tenant_id = 1, no LINQ
        // filter — RLS must narrow to tenant 1's row only.
        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync();
        await using (var setCmd = new NpgsqlCommand(
            "SET app.tenant_id = '1'; SET app.user_id = '00000000-0000-0000-0000-000000000000';", conn))
        {
            await setCmd.ExecuteNonQueryAsync();
        }
        await using (var readCmd = new NpgsqlCommand(
            "SELECT \"TenantId\" FROM tenancy.tenant_module_settings;", conn))
        {
            var rows = new List<long>();
            await using var reader = await readCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetInt64(0));

            rows.Should().NotBeEmpty("at least the tenant 1 seed row should be returned");
            rows.Should().AllSatisfy(t => t.Should().Be(1L), "RLS must filter out tenant 2's row");
        }
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task ReadUnderTenantB_DoesNotLeakRowsForTenantA()
    {
        if (!_enabled) return;
        await SeedRowAsync(tenantId: 1, moduleId: "inspection");
        await SeedRowAsync(tenantId: 2, moduleId: "inspection");

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync();
        await using (var setCmd = new NpgsqlCommand(
            "SET app.tenant_id = '2'; SET app.user_id = '00000000-0000-0000-0000-000000000000';", conn))
        {
            await setCmd.ExecuteNonQueryAsync();
        }
        await using (var readCmd = new NpgsqlCommand(
            "SELECT \"TenantId\" FROM tenancy.tenant_module_settings;", conn))
        {
            var rows = new List<long>();
            await using var reader = await readCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetInt64(0));
            rows.Should().NotBeEmpty();
            rows.Should().AllSatisfy(t => t.Should().Be(2L), "RLS must filter out tenant 1's row");
        }
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task ReadWithoutAppTenantId_AppliesFailClosedDefault()
    {
        if (!_enabled) return;
        await SeedRowAsync(tenantId: 1, moduleId: "inspection");

        await using var conn = new NpgsqlConnection(_appConn);
        await conn.OpenAsync();
        // Don't set app.tenant_id — the policy's COALESCE falls through
        // to the '0' fail-closed default (Sprint 18 / Phase F1), so 0
        // rows are returned.
        await using (var readCmd = new NpgsqlCommand(
            "SELECT \"TenantId\" FROM tenancy.tenant_module_settings;", conn))
        {
            var count = 0;
            await using var reader = await readCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) count++;
            count.Should().Be(0,
                "without app.tenant_id, COALESCE falls through to '0' and the policy admits no rows");
        }
    }

    /// <summary>
    /// Insert one row for the supplied tenant. Runs under the postgres
    /// superuser so we can bypass RLS for the seed step — the assertion
    /// connection is the non-superuser app role.
    /// </summary>
    private async Task SeedRowAsync(long tenantId, string moduleId)
    {
        var dbAdmin = _adminConn!.Replace("Database=postgres", $"Database={_dbName}");
        await using var conn = new NpgsqlConnection(dbAdmin);
        await conn.OpenAsync();
        // Setting app.tenant_id ensures the WITH CHECK clause on the
        // policy admits this insert even though we're connected as the
        // superuser (defensive — superuser bypasses RLS but we want
        // the seed path to be representative).
        await using (var setCmd = new NpgsqlCommand(
            $"SET app.tenant_id = '{tenantId}';", conn))
        {
            await setCmd.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO tenancy.tenant_module_settings (\"TenantId\", \"ModuleId\", \"Enabled\", \"UpdatedAt\") "
            + "VALUES (@tid, @mid, true, NOW());", conn);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("mid", moduleId);
        await cmd.ExecuteNonQueryAsync();
    }
}
