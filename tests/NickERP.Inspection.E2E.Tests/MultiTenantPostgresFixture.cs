using Npgsql;

namespace NickERP.Inspection.E2E.Tests;

/// <summary>
/// Sprint E1 — DB fixture for the multi-location federation test. Stands
/// up the same per-run unique-suffixed platform + inspection database pair
/// as <see cref="PostgresFixture"/>, but ALSO ensures the cluster-wide
/// <c>nscim_app</c> role has a known password so the test can boot the
/// host as <c>nscim_app</c> (the H3 production posture) instead of
/// <c>postgres</c>.
///
/// <para>
/// Docker is unavailable on this build host (per F2's environment audit —
/// same constraint <see cref="PostgresFixture"/> hits), so this falls back
/// to the dev Postgres on <c>localhost:5432</c>. The role itself is
/// cluster-scoped and will already exist if a prior dev cycle ran the F5
/// migrations or this fixture's set-up — both paths are idempotent.
/// </para>
///
/// <para>
/// Connection-string roster:
/// <list type="bullet">
///   <item><c>PlatformAdminConnectionString</c> / <c>InspectionAdminConnectionString</c>:
///         <c>Username=postgres</c> — used by migrations + test seeding +
///         RLS-bypass verification queries.</item>
///   <item><c>PlatformAppConnectionString</c> / <c>InspectionAppConnectionString</c>:
///         <c>Username=nscim_app</c> — used by the host (matches
///         production after H3) AND the DB-layer RLS canary.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class MultiTenantPostgresFixture : IAsyncDisposable
{
    private const string AdminTemplate =
        "Host=localhost;Port=5432;Database={0};Username=postgres;Password={1};Pooling=false";
    private const string AppTemplate =
        "Host=localhost;Port=5432;Database={0};Username=nscim_app;Password={1};Pooling=false";

    public string PlatformDbName { get; }
    public string InspectionDbName { get; }
    public string PlatformAdminConnectionString { get; }
    public string InspectionAdminConnectionString { get; }
    public string PlatformAppConnectionString { get; }
    public string InspectionAppConnectionString { get; }

    private readonly string _adminConnectionString;
    private bool _disposed;

    private MultiTenantPostgresFixture(string adminPassword, string appPassword, string suffix)
    {
        _adminConnectionString = string.Format(AdminTemplate, "postgres", adminPassword);
        PlatformDbName = $"nickerp_e2e_e1_{suffix}_platform";
        InspectionDbName = $"nickerp_e2e_e1_{suffix}_inspection";

        PlatformAdminConnectionString = string.Format(AdminTemplate, PlatformDbName, adminPassword);
        InspectionAdminConnectionString = string.Format(AdminTemplate, InspectionDbName, adminPassword);
        PlatformAppConnectionString = string.Format(AppTemplate, PlatformDbName, appPassword);
        InspectionAppConnectionString = string.Format(AppTemplate, InspectionDbName, appPassword);
    }

    /// <summary>
    /// Spin up a fresh DB pair. Throws if <c>NICKSCAN_DB_PASSWORD</c> is
    /// unset — same loud-fail as <see cref="PostgresFixture"/>.
    ///
    /// <para>
    /// Per the dev convention documented in <c>TESTING.md</c>,
    /// <c>nscim_app</c>'s password equals <c>NICKSCAN_DB_PASSWORD</c>.
    /// We don't rotate it here (touching the cluster-wide role would
    /// break a parallel dev host on :5410); instead we trust the dev
    /// convention and use the same value for both connections.
    /// </para>
    /// </summary>
    public static async Task<MultiTenantPostgresFixture> CreateAsync(CancellationToken ct = default)
    {
        var password = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                "NICKSCAN_DB_PASSWORD env var is required for E2E tests. "
                + "Set it to the dev Postgres password (see TESTING.md §One-time setup). "
                + "The dev convention is that nscim_app's password equals "
                + "NICKSCAN_DB_PASSWORD; the test relies on that to avoid "
                + "rotating the cluster-wide role's password.");
        }

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        var fixture = new MultiTenantPostgresFixture(password, password, suffix);
        await fixture.CreateDatabasesAsync(ct);
        return fixture;
    }

    private async Task CreateDatabasesAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync(ct);

        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{PlatformDbName}\";", conn))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{InspectionDbName}\";", conn))
            await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Verify that <c>nscim_app</c>'s password actually equals
    /// <c>NICKSCAN_DB_PASSWORD</c> by attempting an auth-test connection.
    /// Surfaces a clear error if the dev convention has been broken
    /// instead of letting the host fail at startup with a vague
    /// "password authentication failed" stack trace.
    /// </summary>
    public async Task EnsureNscimAppLoginAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(InspectionAppConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            await cmd.ExecuteScalarAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "nscim_app could not log in with NICKSCAN_DB_PASSWORD. The dev "
                + "convention is that nscim_app's password equals NICKSCAN_DB_PASSWORD. "
                + "Run tools/migrations/phase-f5/set-nscim-app-password.sh to rotate.",
                ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

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

            // Don't drop nscim_app — it's cluster-scoped and shared
            // with any concurrent dev host on :5410.
        }
        catch
        {
            // Tear-down best-effort; leftover DBs get prefixed
            // `nickerp_e2e_e1_*` so manual cleanup can sweep them.
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
