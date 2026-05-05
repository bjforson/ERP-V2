using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Entities;
using Npgsql;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 51 / Phase A — FU-export-postgres-integration-tests.
/// <see cref="TenantExportBundleBuilder.BuildAsync"/> reads via raw
/// <see cref="NpgsqlConnection"/>, so it cannot be exercised against
/// the EF in-memory provider. These integration tests stand up a real
/// Postgres database, install a tiny tenant-owned table directly, and
/// drive <c>BuildAsync</c> end-to-end. Marked
/// <c>[Trait("Category", "RequiresLiveDb")]</c> so CI without
/// <c>NICKSCAN_DB_PASSWORD</c> set passes the test as a no-op (logged,
/// not failed) and the dev box runs the real assertions.
/// </summary>
/// <remarks>
/// <para>
/// Pattern mirrors <see cref="TenantModuleSettingsRlsIntegrationTests"/> +
/// <c>NickERP.Inspection.E2E.Tests.PostgresFixture</c>: per-test DB on
/// <c>localhost:5432</c> via the postgres superuser, GUID-suffixed for
/// concurrent runs, dropped on teardown.
/// </para>
/// <para>
/// We don't apply the full inspection / nickfinance migrations — the
/// surface under test is the bundle builder's read path. Instead we
/// install a single per-test "synthetic_export.cases" table that has
/// a TenantId column matching the tenancy contract, point the export
/// options at it via the override knobs (<c>InspectionTables</c>),
/// seed a couple of rows, and let the builder do the rest.
/// </para>
/// <para>
/// Three test cases here:
/// <list type="bullet">
///   <item>Empty tenant → bundle builds clean (manifest only).</item>
///   <item>Populated tenant → all three formats (Json + CsvFlat + Sql)
///     produce zip entries with content; sha256 + size verified.</item>
///   <item>Cross-tenant isolation → an export for tenant A doesn't
///     include tenant B's rows even when the connection only sets
///     <c>app.tenant_id</c> to the export target. The bundle builder
///     does a defensive <c>WHERE TenantId = @tid</c> AND sets the
///     session var; this test verifies neither half leaks the wrong
///     tenant's rows.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class TenantExportBundleBuilderIntegrationTests : IAsyncLifetime
{
    private string? _adminConn;
    private string? _testDbConn;
    private string? _dbName;
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
        _dbName = $"nickerp_test_{suffix}_export";
        _adminConn =
            $"Host=localhost;Port=5432;Database=postgres;Username=postgres;Password={password};Pooling=false";
        _testDbConn =
            $"Host=localhost;Port=5432;Database={_dbName};Username=postgres;Password={password};Pooling=false";

        await using (var conn = new NpgsqlConnection(_adminConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_dbName}\";", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Install a synthetic schema + table the bundle builder can read
        // through. Mirrors the inspection/nickfinance contract: TenantId
        // bigint + a couple of payload columns. We don't enable RLS — the
        // bundle builder applies its own WHERE filter, and exercising the
        // RLS path is already covered by TenantModuleSettingsRlsIntegrationTests.
        await using (var conn = new NpgsqlConnection(_testDbConn))
        {
            await conn.OpenAsync();
            var stmts = new[]
            {
                "CREATE SCHEMA synthetic_export;",
                @"CREATE TABLE synthetic_export.cases (
                    ""Id"" uuid PRIMARY KEY,
                    ""TenantId"" bigint NOT NULL,
                    ""Code"" text NOT NULL,
                    ""Payload"" jsonb,
                    ""CreatedAt"" timestamptz NOT NULL DEFAULT NOW()
                );",
                @"CREATE TABLE synthetic_export.events (
                    ""Id"" uuid PRIMARY KEY,
                    ""TenantId"" bigint NOT NULL,
                    ""EventType"" text NOT NULL,
                    ""OccurredAt"" timestamptz NOT NULL DEFAULT NOW()
                );",
            };
            foreach (var s in stmts)
            {
                await using var cmd = new NpgsqlCommand(s, conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        _tempRoot = Path.Combine(Path.GetTempPath(), "tenant-export-int-tests-" + Guid.NewGuid().ToString("N"));
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
            await using (var cmd = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();", conn))
            {
                cmd.Parameters.AddWithValue("db", _dbName!);
                try { await cmd.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
            }
            await using (var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE);", conn))
                await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"TenantExportBundleBuilderIntegrationTests teardown: {ex}");
        }
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task BuildAsync_EmptyTenant_BuildsManifestOnlyBundle()
    {
        if (!_enabled) return;

        var outputPath = Path.Combine(_tempRoot!, "empty.zip");
        var request = NewRequest(tenantId: 42, format: TenantExportFormat.JsonBundle);
        var options = new TenantExportOptions
        {
            InspectionConnectionString = _testDbConn,
            // Point at the synthetic schema. Bundle builder will run
            // SELECT against it; tenant 42 has zero rows so each table
            // file will be the empty-array sentinel.
            InspectionTables = new[] { "synthetic_export.cases", "synthetic_export.events" },
        };

        var (size, sha) = await TenantExportBundleBuilder.BuildAsync(
            outputPath, tenantId: 42, request, options,
            NullLogger<TenantExportBundleBuilderIntegrationTests>.Instance, CancellationToken.None);

        size.Should().BeGreaterThan(0);
        sha.Should().NotBeNull().And.HaveCount(32);
        File.Exists(outputPath).Should().BeTrue();

        await using var fs = File.OpenRead(outputPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        zip.Entries.Should().Contain(e => e.FullName == "manifest.json");
        // Empty tenant → both tables write `[]` (json) entries; not error
        // entries. Empty-array sentinel proves the read happened cleanly.
        zip.Entries.Should().Contain(e => e.FullName == "inspection/synthetic_export.cases.json");
        zip.Entries.Should().Contain(e => e.FullName == "inspection/synthetic_export.events.json");
        zip.Entries.Should().NotContain(e => e.FullName.EndsWith(".error.txt"));

        var casesEntry = zip.GetEntry("inspection/synthetic_export.cases.json")!;
        await using var es = casesEntry.Open();
        using var sr = new StreamReader(es);
        var json = await sr.ReadToEndAsync();
        json.Trim().Should().Be("[]");
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task BuildAsync_PopulatedTenant_AllThreeFormatsRoundtripCleanly()
    {
        if (!_enabled) return;
        await SeedRowAsync(tenantId: 7, code: "C-7-A", payload: "{\"value\":1}");
        await SeedRowAsync(tenantId: 7, code: "C-7-B", payload: "{\"value\":2}");

        foreach (var format in new[] { TenantExportFormat.JsonBundle, TenantExportFormat.CsvFlat, TenantExportFormat.Sql })
        {
            var outputPath = Path.Combine(_tempRoot!, $"populated-{format}.zip");
            var request = NewRequest(tenantId: 7, format);
            var options = new TenantExportOptions
            {
                InspectionConnectionString = _testDbConn,
                InspectionTables = new[] { "synthetic_export.cases" },
            };

            var (size, sha) = await TenantExportBundleBuilder.BuildAsync(
                outputPath, tenantId: 7, request, options,
                NullLogger<TenantExportBundleBuilderIntegrationTests>.Instance, CancellationToken.None);

            size.Should().BeGreaterThan(0);
            sha.Should().HaveCount(32);
            // Re-compute sha of the on-disk bytes — proves the reported
            // sha matches the persisted artifact.
            byte[] onDiskSha;
            await using (var f = File.OpenRead(outputPath))
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                onDiskSha = await sha256.ComputeHashAsync(f);
            }
            onDiskSha.Should().Equal(sha, $"BuildAsync's reported sha must match on-disk bytes for {format}");

            await using var fs = File.OpenRead(outputPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var ext = format switch
            {
                TenantExportFormat.JsonBundle => "json",
                TenantExportFormat.CsvFlat => "csv",
                TenantExportFormat.Sql => "sql",
                _ => "json"
            };
            var entry = zip.GetEntry($"inspection/synthetic_export.cases.{ext}");
            entry.Should().NotBeNull($"format {format} must produce inspection/synthetic_export.cases.{ext}");

            await using var es = entry!.Open();
            using var sr = new StreamReader(es);
            var contents = await sr.ReadToEndAsync();
            contents.Should().Contain("C-7-A");
            contents.Should().Contain("C-7-B");
        }
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task BuildAsync_CrossTenantIsolation_BundleHoldsOnlyTargetTenantRows()
    {
        if (!_enabled) return;

        // Two tenants, three rows in tenant 1, one row in tenant 99.
        await SeedRowAsync(tenantId: 1, code: "T1-A", payload: "{\"v\":\"a\"}");
        await SeedRowAsync(tenantId: 1, code: "T1-B", payload: "{\"v\":\"b\"}");
        await SeedRowAsync(tenantId: 1, code: "T1-C", payload: "{\"v\":\"c\"}");
        await SeedRowAsync(tenantId: 99, code: "T99-LEAK", payload: "{\"v\":\"leak\"}");

        var outputPath = Path.Combine(_tempRoot!, "iso-tenant1.zip");
        var request = NewRequest(tenantId: 1, format: TenantExportFormat.JsonBundle);
        var options = new TenantExportOptions
        {
            InspectionConnectionString = _testDbConn,
            InspectionTables = new[] { "synthetic_export.cases" },
        };

        var (size, _) = await TenantExportBundleBuilder.BuildAsync(
            outputPath, tenantId: 1, request, options,
            NullLogger<TenantExportBundleBuilderIntegrationTests>.Instance, CancellationToken.None);
        size.Should().BeGreaterThan(0);

        await using var fs = File.OpenRead(outputPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("inspection/synthetic_export.cases.json")!;
        await using var es = entry.Open();
        using var sr = new StreamReader(es);
        var json = await sr.ReadToEndAsync();

        // The exported bundle must include all three tenant 1 rows AND
        // none of tenant 99's. Even with the connection-level system
        // context flip used elsewhere on the platform, the bundle
        // builder's WHERE @tid filter is the load-bearing isolation.
        json.Should().Contain("T1-A");
        json.Should().Contain("T1-B");
        json.Should().Contain("T1-C");
        json.Should().NotContain("T99-LEAK", "tenant-99 row must not appear in tenant-1's export bundle");
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task ListenNotify_RoundTrip_DeliversWithinOneSecond()
    {
        // Sprint 51 / Phase B — verify that issuing a NOTIFY on the
        // dedicated channel reaches a LISTEN-ing connection within ~1 s.
        // This is the underlying primitive both halves of the Phase B
        // wireup depend on; the runner tests cover the dispatch
        // semantics, this one proves the channel name + payload flow.
        if (!_enabled) return;

        await using var listenConn = new NpgsqlConnection(_testDbConn);
        await listenConn.OpenAsync();
        var channel = NickERP.Platform.Tenancy.Database.Workers.TenantExportRunner.NotifyChannel;
        await using (var cmd = listenConn.CreateCommand())
        {
            cmd.CommandText = $"LISTEN {channel};";
            await cmd.ExecuteNonQueryAsync();
        }
        var received = new TaskCompletionSource<string>();
        listenConn.Notification += (_, e) =>
        {
            if (string.Equals(e.Channel, channel, StringComparison.Ordinal))
            {
                received.TrySetResult(e.Payload);
            }
        };

        // WaitAsync drains pending notifications; run in the background.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var listenTask = Task.Run(async () =>
        {
            try { while (!cts.IsCancellationRequested) await listenConn.WaitAsync(cts.Token); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        });

        await Task.Delay(50);

        // Issue the NOTIFY on a separate connection.
        await using (var pubConn = new NpgsqlConnection(_testDbConn))
        {
            await pubConn.OpenAsync();
            var payload = "abc123";
            await using var cmd = pubConn.CreateCommand();
            cmd.CommandText = $"NOTIFY {channel}, '{payload}';";
            await cmd.ExecuteNonQueryAsync();
        }

        var deliveredTask = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        deliveredTask.Should().Be(received.Task,
            "the NOTIFY should reach the LISTEN connection inside 2 s; the runner uses the same primitive for sub-second dispatch");
        var payloadReceived = await received.Task;
        payloadReceived.Should().Be("abc123");

        cts.Cancel();
        try { await listenTask; } catch { /* best-effort */ }
    }

    private async Task SeedRowAsync(long tenantId, string code, string payload)
    {
        await using var conn = new NpgsqlConnection(_testDbConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO synthetic_export.cases (\"Id\", \"TenantId\", \"Code\", \"Payload\") "
            + "VALUES (@id, @tid, @code, @payload::jsonb);", conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("payload", payload);
        await cmd.ExecuteNonQueryAsync();
    }

    private static TenantExportRequest NewRequest(long tenantId, TenantExportFormat format)
    {
        return new TenantExportRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RequestedAt = DateTimeOffset.UtcNow,
            RequestedByUserId = Guid.NewGuid(),
            Format = format,
            Scope = TenantExportScope.InspectionOnly,
            Status = TenantExportStatus.Running,
        };
    }
}
