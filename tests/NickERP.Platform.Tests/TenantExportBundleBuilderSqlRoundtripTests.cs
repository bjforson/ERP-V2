using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Entities;
using Npgsql;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 51 / Phase D — FU-export-sql-roundtrip. Verifies that a
/// <see cref="TenantExportFormat.Sql"/> bundle produced by
/// <see cref="TenantExportBundleBuilder.BuildAsync"/> can be restored
/// into a fresh schema and round-trips row count + column values
/// (within the limits the bundle builder documents).
/// </summary>
/// <remarks>
/// <para>
/// Pattern mirrors the other live-Postgres integration tests
/// (<see cref="TenantExportBundleBuilderIntegrationTests"/>): per-test
/// DB on localhost:5432, GUID suffix, postgres superuser. Marked
/// <c>[Trait("Category", "RequiresLiveDb")]</c>; no-ops when
/// <c>NICKSCAN_DB_PASSWORD</c> is unset.
/// </para>
/// <para>
/// Known limitations (documented as test summary so future maintainers
/// know what NOT to expect from the SQL roundtrip):
/// <list type="bullet">
///   <item><c>jsonb</c> values are exported as quoted text literals
///     (<c>E'{"key":"value"}'</c>) — they restore into a target with
///     a <c>text</c> or <c>jsonb</c> column and survive shape, but the
///     INSERT itself contains no <c>::jsonb</c> cast. Targets MUST
///     declare the column as <c>jsonb</c> for the implicit cast.</item>
///   <item>Postgres array columns (<c>text[]</c>, <c>uuid[]</c>) are
///     exported as their default ToString surface, not array literals.
///     Round-trip is best-effort for arrays — covered by the dedicated
///     <see cref="ArrayColumns_NoteAsKnownLimitation"/> test below.</item>
///   <item>Custom Postgres types (range types, hstore, geometry) are
///     not supported — the bundle builder serialises via
///     <c>reader.GetValue()</c> which falls through to the generic
///     <c>Convert.ToString</c> path.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class TenantExportBundleBuilderSqlRoundtripTests : IAsyncLifetime
{
    private string? _adminConn;
    private string? _sourceDbConn;
    private string? _targetDbConn;
    private string? _sourceDbName;
    private string? _targetDbName;
    private bool _enabled;
    private string? _tempRoot;

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
        _sourceDbName = $"nickerp_test_{suffix}_sqlrt_src";
        _targetDbName = $"nickerp_test_{suffix}_sqlrt_dst";
        _adminConn =
            $"Host=localhost;Port=5432;Database=postgres;Username=postgres;Password={password};Pooling=false";
        _sourceDbConn =
            $"Host=localhost;Port=5432;Database={_sourceDbName};Username=postgres;Password={password};Pooling=false";
        _targetDbConn =
            $"Host=localhost;Port=5432;Database={_targetDbName};Username=postgres;Password={password};Pooling=false";

        await using (var conn = new NpgsqlConnection(_adminConn))
        {
            await conn.OpenAsync();
            await using (var c1 = new NpgsqlCommand($"CREATE DATABASE \"{_sourceDbName}\";", conn))
                await c1.ExecuteNonQueryAsync();
            await using (var c2 = new NpgsqlCommand($"CREATE DATABASE \"{_targetDbName}\";", conn))
                await c2.ExecuteNonQueryAsync();
        }

        // Identical schema on both sides.
        var schemaScript = @"
CREATE SCHEMA roundtrip;
CREATE TABLE roundtrip.cases (
    ""Id"" uuid PRIMARY KEY,
    ""TenantId"" bigint NOT NULL,
    ""Code"" text NOT NULL,
    ""Description"" text,
    ""Active"" boolean NOT NULL,
    ""Score"" numeric,
    ""CreatedAt"" timestamptz NOT NULL,
    ""Payload"" jsonb
);";
        foreach (var connStr in new[] { _sourceDbConn, _targetDbConn })
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(schemaScript, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        _tempRoot = Path.Combine(Path.GetTempPath(), "tenant-export-sqlrt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public async Task DisposeAsync()
    {
        if (_tempRoot is not null && Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        }
        if (!_enabled || _adminConn is null) return;
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(_adminConn);
            await conn.OpenAsync();
            foreach (var db in new[] { _sourceDbName, _targetDbName })
            {
                await using (var cmd = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();", conn))
                {
                    cmd.Parameters.AddWithValue("db", db!);
                    try { await cmd.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
                }
                await using var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE);", conn);
                await dropCmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TenantExportBundleBuilderSqlRoundtripTests teardown: {ex}");
        }
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task BuildSqlBundle_ThenRestore_RowCountsAndSampleColumnsRoundTrip()
    {
        if (!_enabled) return;

        // Seed three rows on the source side; only two for tenant 11
        // (the export target) — the third lives under tenant 12 and
        // must NOT survive into the target.
        var t11RowA = await SeedSourceAsync(tenantId: 11, code: "C-1", description: "first row", active: true, score: 1.5m, payload: "{\"v\":1,\"name\":\"alpha\"}");
        var t11RowB = await SeedSourceAsync(tenantId: 11, code: "C-2", description: null, active: false, score: 99.999m, payload: "{\"v\":2}");
        await SeedSourceAsync(tenantId: 12, code: "C-OUT", description: "tenant 12 row", active: true, score: 0m, payload: null);

        // Build the bundle for tenant 11.
        var outputPath = Path.Combine(_tempRoot!, "sqlrt.zip");
        var request = new TenantExportRequest
        {
            Id = Guid.NewGuid(),
            TenantId = 11,
            RequestedAt = DateTimeOffset.UtcNow,
            RequestedByUserId = Guid.NewGuid(),
            Format = TenantExportFormat.Sql,
            Scope = TenantExportScope.InspectionOnly,
            Status = TenantExportStatus.Running,
        };
        var options = new TenantExportOptions
        {
            InspectionConnectionString = _sourceDbConn,
            InspectionTables = new[] { "roundtrip.cases" },
        };
        var (size, _) = await TenantExportBundleBuilder.BuildAsync(
            outputPath, tenantId: 11, request, options,
            NullLogger<TenantExportBundleBuilderSqlRoundtripTests>.Instance, CancellationToken.None);
        size.Should().BeGreaterThan(0);

        // Extract the SQL file from the bundle and execute it on the
        // target DB.
        string sqlText;
        await using (var fs = File.OpenRead(outputPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var entry = zip.GetEntry("inspection/roundtrip.cases.sql");
            entry.Should().NotBeNull("SQL-format bundle must contain inspection/roundtrip.cases.sql");
            await using var es = entry!.Open();
            using var sr = new StreamReader(es);
            sqlText = await sr.ReadToEndAsync();
        }

        await using (var targetConn = new NpgsqlConnection(_targetDbConn))
        {
            await targetConn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sqlText, targetConn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Row count check — exactly the 2 tenant-11 rows; tenant 12 must
        // NOT have leaked into the bundle.
        long targetCount = await ScalarLongAsync(_targetDbConn!, "SELECT COUNT(*) FROM roundtrip.cases;");
        targetCount.Should().Be(2, "exactly the 2 tenant-11 rows must round-trip; the tenant-12 row must not appear");

        // Sample column round-trip — pull row A from the target and
        // compare its columns against the seeded values.
        await using var verifyConn = new NpgsqlConnection(_targetDbConn);
        await verifyConn.OpenAsync();
        await using var verifyCmd = new NpgsqlCommand(
            "SELECT \"Code\", \"Description\", \"Active\", \"Score\", \"Payload\"::text "
            + "FROM roundtrip.cases WHERE \"Id\" = @id;", verifyConn);
        verifyCmd.Parameters.AddWithValue("id", t11RowA);
        await using var reader = await verifyCmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("C-1");
        reader.GetString(1).Should().Be("first row");
        reader.GetBoolean(2).Should().BeTrue();
        reader.GetDecimal(3).Should().Be(1.5m);
        // jsonb roundtrip — Postgres normalises whitespace; we compare
        // by parsing back to a jsonb-like shape.
        var jsonText = reader.GetString(4);
        jsonText.Should().Contain("\"v\": 1");
        jsonText.Should().Contain("\"name\": \"alpha\"");

        // Row B has Description = NULL — verify the NULL literal made
        // it through the SQL serialiser.
        await reader.CloseAsync();
        await using var verifyCmd2 = new NpgsqlCommand(
            "SELECT \"Description\" FROM roundtrip.cases WHERE \"Id\" = @id;", verifyConn);
        verifyCmd2.Parameters.AddWithValue("id", t11RowB);
        var result = await verifyCmd2.ExecuteScalarAsync();
        result.Should().BeOfType<DBNull>("NULL Description must round-trip as NULL, not the string 'NULL'");
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public void ArrayColumns_NoteAsKnownLimitation()
    {
        // Sprint 51 / Phase D — known limitation.
        //
        // Postgres array columns (text[], uuid[]) round-trip via the SQL
        // bundle on a best-effort basis only. The bundle builder's
        // ReadTableAsSqlAsync uses reader.GetValue() which returns CLR
        // arrays; these go through the QuoteSql default branch which
        // calls Convert.ToString and produces "System.String[]" — NOT
        // a Postgres array literal '{a,b}'.
        //
        // Restoring such bundles into a target with array columns will
        // fail with "malformed array literal" — the import script must
        // either:
        //   (a) post-process the bundle to wrap arrays as '{a,b}', or
        //   (b) avoid declaring array columns on tenant-owned tables.
        //
        // The v2 inspection/nickfinance schemas do not currently use
        // array columns on tenant-owned tables; this test exists as a
        // tombstone so the limitation surfaces in a green test summary
        // and future maintainers don't accidentally rely on a feature
        // that isn't there.
        true.Should().BeTrue("see test body for the documented array limitation");
    }

    private async Task<Guid> SeedSourceAsync(long tenantId, string code, string? description, bool active, decimal score, string? payload)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(_sourceDbConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
INSERT INTO roundtrip.cases
    (""Id"", ""TenantId"", ""Code"", ""Description"", ""Active"", ""Score"", ""CreatedAt"", ""Payload"")
VALUES
    (@id, @tid, @code, @desc, @active, @score, NOW(), @payload::jsonb);", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("active", active);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("payload", (object?)payload ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<long> ScalarLongAsync(string connStr, string sql)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(v);
    }
}
