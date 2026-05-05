using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy.Database;
using NickERP.Tools.PerfSeed;
using Npgsql;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 52 / FU-perf-tenant-data-shape — smoke that the
/// <c>PerfSeeder</c> can seed 10 tenants × 100 cases each (1000 cases
/// total) on a fresh DB without exceptions, with the row-count
/// invariants the brief specified.
/// </summary>
/// <remarks>
/// <para>
/// "Smoke" here means: the seeder reaches every code path (reference
/// data, cases, scans, artifacts, reviews, findings, verdicts, the
/// closing audit event) and the SeedSummary's totals satisfy the
/// invariants in the brief:
/// </para>
/// <list type="bullet">
///   <item><c>TenantCount = 10</c></item>
///   <item><c>CaseCount = 1000</c></item>
///   <item><c>ScanCount = CaseCount * 1..3</c></item>
///   <item><c>ArtifactCount = ScanCount</c></item>
///   <item><c>ReviewCount, FindingCount, VerdictCount</c> all > 0</item>
///   <item>Exactly 1 <c>nickerp.perf.seeded</c> audit event written</item>
/// </list>
/// <para>
/// Same skip-by-pass pattern as the rest of the platform tests:
/// <c>NICKSCAN_DB_PASSWORD</c> not set → silent return. CI without dev
/// Postgres passes.
/// </para>
/// </remarks>
public sealed class PerfSeederSmokeTests : IAsyncLifetime
{
    private string? _adminConn;
    private string? _platformDb;
    private string? _inspectionDb;
    private string? _password;
    private bool _enabled;

    public async Task InitializeAsync()
    {
        _password = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
        if (string.IsNullOrEmpty(_password))
        {
            _enabled = false;
            return;
        }
        _enabled = true;

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        _platformDb = $"nickerp_test_{suffix}_platform";
        _inspectionDb = $"nickerp_test_{suffix}_inspection";
        _adminConn =
            $"Host=localhost;Port=5432;Database=postgres;Username=postgres;Password={_password};Pooling=false";

        // Stand up the throwaway DBs.
        await using (var conn = new NpgsqlConnection(_adminConn))
        {
            await conn.OpenAsync();
            await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_platformDb}\";", conn))
                await cmd.ExecuteNonQueryAsync();
            await using (var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_inspectionDb}\";", conn))
                await cmd.ExecuteNonQueryAsync();
        }

        // Ensure the cluster-wide nscim_app role exists so the audit
        // migration's GRANTs land cleanly.
        await using (var conn = new NpgsqlConnection(_adminConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app') THEN
        CREATE ROLE nscim_app WITH LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD 'testonly';
    END IF;
END $$;", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Apply migrations.
        var platformAdmin =
            $"Host=localhost;Port=5432;Database={_platformDb};Username=postgres;Password={_password};Pooling=false";
        var inspectionAdmin =
            $"Host=localhost;Port=5432;Database={_inspectionDb};Username=postgres;Password={_password};Pooling=false";

        Environment.SetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION", platformAdmin);
        Environment.SetEnvironmentVariable("NICKERP_INSPECTION_DB_CONNECTION", inspectionAdmin);

        await using (var ctx = new TenancyDbContextFactory().CreateDbContext(Array.Empty<string>()))
            await ctx.Database.MigrateAsync();
        await using (var ctx = new AuditDbContextFactory().CreateDbContext(Array.Empty<string>()))
            await ctx.Database.MigrateAsync();
        await using (var ctx = new NickERP.Inspection.Database.InspectionDbContextFactory()
            .CreateDbContext(Array.Empty<string>()))
            await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_enabled || _adminConn is null) return;
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(_adminConn);
            await conn.OpenAsync();
            foreach (var db in new[] { _platformDb, _inspectionDb })
            {
                await using (var cmd = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();", conn))
                {
                    cmd.Parameters.AddWithValue("db", db!);
                    try { await cmd.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
                }
                await using (var cmd = new NpgsqlCommand(
                    $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE);", conn))
                    await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"PerfSeederSmokeTests teardown: {ex}");
        }
    }

    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task Seed_TenTenantsHundredCasesEach_ProducesExpectedRowCounts()
    {
        if (!_enabled) return;

        var platformConn =
            $"Host=localhost;Port=5432;Database={_platformDb};Username=postgres;Password={_password};Pooling=false";
        var inspectionConn =
            $"Host=localhost;Port=5432;Database={_inspectionDb};Username=postgres;Password={_password};Pooling=false";

        // Deterministic seed so the test is reproducible across CI hosts.
        var seeder = new PerfSeeder(platformConn, inspectionConn, randomSeed: 42);

        var summary = await seeder.SeedAsync(casesPerTenant: 100, tenantCount: 10);

        // Brief invariants:
        summary.TenantCount.Should().Be(10);
        summary.CaseCount.Should().Be(1000);

        // Per case 1-3 scans → ScanCount in [1000, 3000].
        summary.ScanCount.Should().BeInRange(1000, 3000);
        summary.ArtifactCount.Should().Be(summary.ScanCount,
            "every scan produces exactly one ScanArtifact in the seeder");

        // 0-2 reviews per case ⇒ on a 1000-case run, at least one
        // bucket must produce a non-zero count for the random sampling
        // to look reasonable.
        summary.ReviewCount.Should().BeGreaterThan(0,
            "with 1000 cases × Random(0..2 reviews), zero reviews is implausible");

        // Findings only attach to reviews; with no reviews zero findings
        // is fine, but with reviews > 0 we expect non-zero findings on
        // the seeded sample.
        summary.FindingCount.Should().BeGreaterThan(0,
            "the random distribution of 0-5 findings across reviews should produce a non-zero total");

        // ~80% of 1000 cases are in the verdict-rendered+ buckets.
        summary.VerdictCount.Should().BeGreaterThan(700);

        // The audit event was emitted.
        await using var conn = new NpgsqlConnection(platformConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
SELECT count(*)
  FROM audit.events
 WHERE ""EventType"" = 'nickerp.perf.seeded'
   AND ""EntityId"" = @run;", conn);
        cmd.Parameters.AddWithValue("run", summary.RunId.ToString("N"));
        var auditCount = (long)(await cmd.ExecuteScalarAsync())!;
        auditCount.Should().Be(1L,
            "PerfSeeder must emit exactly one nickerp.perf.seeded audit event per run");
    }
}
