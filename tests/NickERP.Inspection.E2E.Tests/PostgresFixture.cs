using Npgsql;

namespace NickERP.Inspection.E2E.Tests;

/// <summary>
/// Stands up two unique-named databases on the dev Postgres
/// (<c>localhost:5432</c>) for one test run: one for the platform schemas
/// (Identity + Tenancy + Audit) and one for the Inspection schema. The
/// DB names are GUID-suffixed so concurrent test runs don't collide and
/// leftover state from a crashed run can be cleaned up out-of-band.
///
/// Docker is unavailable on this build host (F2's environment audit
/// flagged the same), so Testcontainers is not an option. The fallback
/// here matches the brief's instruction: "use the dev Postgres on
/// localhost:5432 with a unique-suffixed schema-per-test approach".
///
/// The fixture also creates a non-superuser role (<c>nickerp_e2e_rls_*</c>)
/// scoped to the inspection DB and granted SELECT on every inspection
/// table. The e2e test uses that role to prove RLS actually filters —
/// the default <c>postgres</c> connection has <c>BYPASSRLS</c> set and
/// would silently see every row regardless of <c>app.tenant_id</c>.
/// </summary>
internal sealed class PostgresFixture : IAsyncDisposable
{
    private const string AdminConnectionStringFormat =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password={0};Pooling=false";

    public string PlatformDbName { get; }
    public string InspectionDbName { get; }
    public string RlsRoleName { get; }
    public string RlsRolePassword { get; }
    public string PlatformConnectionString { get; }
    public string InspectionConnectionString { get; }
    public string InspectionRlsConnectionString { get; }

    private readonly string _adminConnectionString;
    private bool _disposed;

    private PostgresFixture(string password, string suffix)
    {
        _adminConnectionString = string.Format(AdminConnectionStringFormat, password);
        PlatformDbName = $"nickerp_e2e_{suffix}_platform";
        InspectionDbName = $"nickerp_e2e_{suffix}_inspection";
        RlsRoleName = $"nickerp_e2e_{suffix}_rls";
        // Random per-run password — short-lived, dropped on cleanup.
        RlsRolePassword = "rls_" + Guid.NewGuid().ToString("N");

        PlatformConnectionString =
            $"Host=localhost;Port=5432;Database={PlatformDbName};Username=postgres;Password={password};Pooling=false";
        InspectionConnectionString =
            $"Host=localhost;Port=5432;Database={InspectionDbName};Username=postgres;Password={password};Pooling=false";
        InspectionRlsConnectionString =
            $"Host=localhost;Port=5432;Database={InspectionDbName};Username={RlsRoleName};Password={RlsRolePassword};Pooling=false";
    }

    /// <summary>
    /// Spin up the two databases. Throws an <see cref="InvalidOperationException"/>
    /// when <c>NICKSCAN_DB_PASSWORD</c> is not set so the e2e test fails
    /// loudly on a misconfigured host instead of timing out.
    /// </summary>
    public static async Task<PostgresFixture> CreateAsync(CancellationToken ct = default)
    {
        var password = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "NICKSCAN_DB_PASSWORD env var is required for E2E tests. "
                + "Set it to the dev Postgres password (see TESTING.md §One-time setup).");
        }

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        var fixture = new PostgresFixture(password, suffix);
        await fixture.CreateDatabasesAsync(ct);
        return fixture;
    }

    private async Task CreateDatabasesAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        // CREATE DATABASE can't run inside a transaction; raw command.
        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{PlatformDbName}\";", conn))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{InspectionDbName}\";", conn))
            await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Grant the RLS role enough privileges to <c>SELECT</c> from every
    /// table in the <c>inspection</c> schema. Called AFTER the inspection
    /// migrations have applied so the schema actually exists.
    /// </summary>
    public async Task PrepareRlsRoleAsync(CancellationToken ct = default)
    {
        // Role is cluster-scoped, not DB-scoped, so create it on the
        // admin connection. NOSUPERUSER NOBYPASSRLS guarantees RLS
        // policies actually run against this connection.
        await using (var conn = new NpgsqlConnection(_adminConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                $"CREATE ROLE \"{RlsRoleName}\" LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RlsRolePassword}';",
                conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Grant per-DB connect + read-only on the inspection schema.
        var inspectionAdmin =
            $"Host=localhost;Port=5432;Database={InspectionDbName};Username=postgres;Password={Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD")};Pooling=false";
        await using (var conn = new NpgsqlConnection(inspectionAdmin))
        {
            await conn.OpenAsync(ct);
            var stmts = new[]
            {
                $"GRANT CONNECT ON DATABASE \"{InspectionDbName}\" TO \"{RlsRoleName}\";",
                $"GRANT USAGE ON SCHEMA inspection TO \"{RlsRoleName}\";",
                $"GRANT SELECT ON ALL TABLES IN SCHEMA inspection TO \"{RlsRoleName}\";",
                $"ALTER DEFAULT PRIVILEGES IN SCHEMA inspection GRANT SELECT ON TABLES TO \"{RlsRoleName}\";",
            };
            foreach (var s in stmts)
            {
                await using var cmd = new NpgsqlCommand(s, conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Best-effort teardown. Force connection close on the test DBs
        // so a stuck connection doesn't block DROP — this is dev-grade
        // tear-down, not production cleanup.
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(_adminConnectionString);
            await conn.OpenAsync();

            await TerminateAsync(conn, PlatformDbName);
            await TerminateAsync(conn, InspectionDbName);

            await using (var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{PlatformDbName}\" WITH (FORCE);", conn))
                await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{InspectionDbName}\" WITH (FORCE);", conn))
                await cmd.ExecuteNonQueryAsync();

            await using (var cmd = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{RlsRoleName}\";", conn))
                await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Tear-down best-effort; leftover DBs/roles get prefixed
            // `nickerp_e2e_*` so a periodic cleanup script can sweep
            // them out-of-band.
        }
    }

    private static async Task TerminateAsync(NpgsqlConnection conn, string dbName)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();",
            conn);
        cmd.Parameters.AddWithValue("db", dbName);
        try { await cmd.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
    }
}
