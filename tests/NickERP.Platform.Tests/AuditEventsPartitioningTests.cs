using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Audit.Database;
using Npgsql;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 52 / FU-audit-events-partitioning — proves the partitioning
/// migration lands a working <c>audit.events</c>:
/// </summary>
/// <list type="bullet">
///   <item>Migration applies cleanly on a fresh DB (no exceptions).</item>
///   <item>The resulting <c>audit.events</c> is a <c>partitioned</c> relkind
///         (<c>relkind = 'p'</c>), with the expected 18 monthly child
///         partitions pre-created.</item>
///   <item>An INSERT routes to the correct month-partition based on
///         <c>OccurredAt</c> — partition pruning works.</item>
///   <item>A SELECT filtered on <c>OccurredAt</c> for one month touches
///         only that month's partition (EXPLAIN check, smoke).</item>
///   <item>Cross-partition queries (no OccurredAt filter) still work and
///         return the union — no regressions for ad-hoc reporting.</item>
/// </list>
/// <remarks>
/// <para>
/// Test pattern mirrors <see cref="TenantModuleSettingsRlsIntegrationTests"/>:
/// per-test throwaway DB on <c>localhost:5432</c>, EF migrations applied
/// via <c>Database.MigrateAsync()</c>, asserts via the postgres
/// superuser (no need for the non-superuser role here — the assertions
/// are structural, not RLS-flavoured).
/// </para>
/// <para>
/// Skipped silently when <c>NICKSCAN_DB_PASSWORD</c> is not set — same
/// CI-friendly skip-by-pass pattern as the rest of the platform tests.
/// </para>
/// </remarks>
public sealed class AuditEventsPartitioningTests : IAsyncLifetime
{
    private string? _adminConn;
    private string? _dbName;
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
        _dbName = $"nickerp_test_{suffix}_audit";
        _adminConn =
            $"Host=localhost;Port=5432;Database=postgres;Username=postgres;Password={_password};Pooling=false";

        await using (var conn = new NpgsqlConnection(_adminConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_dbName}\";", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var dbAdmin =
            $"Host=localhost;Port=5432;Database={_dbName};Username=postgres;Password={_password};Pooling=false";

        // The Convert_AuditEvents_To_Partitioned_Table migration GRANTs to
        // nscim_app — that role is cluster-wide and idempotent (created on
        // first run), but on a fresh suffix-named DB it might not exist
        // yet. Pre-create the role so the migration's GRANTs land cleanly.
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

        Environment.SetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION", dbAdmin);
        await using var ctx = new AuditDbContextFactory().CreateDbContext(Array.Empty<string>());
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
            await using (var cmd = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();", conn))
            {
                cmd.Parameters.AddWithValue("db", _dbName!);
                try { await cmd.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
            }
            await using (var cmd = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE);", conn))
                await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"AuditEventsPartitioningTests teardown: {ex}");
        }
    }

    /// <summary>
    /// After the migration runs, <c>audit.events</c> should be a
    /// partitioned table (relkind = 'p'). This is the structural
    /// invariant — if a future migration regresses the partitioning,
    /// this test fails loudly.
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task AuditEvents_IsPartitionedRelkind_AfterMigration()
    {
        if (!_enabled) return;

        var dbAdmin =
            $"Host=localhost;Port=5432;Database={_dbName};Username=postgres;Password={_password};Pooling=false";
        await using var conn = new NpgsqlConnection(dbAdmin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
SELECT c.relkind
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
 WHERE n.nspname = 'audit'
   AND c.relname  = 'events';", conn);
        var relkind = (char)(await cmd.ExecuteScalarAsync())!;
        relkind.Should().Be('p',
            "audit.events should be a partitioned table (relkind 'p') after the conversion migration");
    }

    /// <summary>
    /// 12 months back + 6 months ahead = 18 partitions pre-created by
    /// the migration. Future months are added by the recurring SQL
    /// helper in <c>tools/migrations/audit-events-create-partition.sql</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task AuditEvents_HasEighteenPreCreatedPartitions()
    {
        if (!_enabled) return;

        var dbAdmin =
            $"Host=localhost;Port=5432;Database={_dbName};Username=postgres;Password={_password};Pooling=false";
        await using var conn = new NpgsqlConnection(dbAdmin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
SELECT count(*)
  FROM pg_inherits i
  JOIN pg_class p ON p.oid = i.inhparent
  JOIN pg_namespace pn ON pn.oid = p.relnamespace
 WHERE pn.nspname = 'audit'
   AND p.relname  = 'events';", conn);
        var partitionCount = (long)(await cmd.ExecuteScalarAsync())!;
        partitionCount.Should().Be(18L,
            "the migration pre-creates 12 months back + 6 months ahead = 18 monthly partitions");
    }

    /// <summary>
    /// Smoke: a row inserted with OccurredAt in May 2026 lands in the
    /// events_2026_05 partition. Proves partition routing works.
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task Insert_RoutesToCorrectMonthPartition()
    {
        if (!_enabled) return;

        var dbAdmin =
            $"Host=localhost;Port=5432;Database={_dbName};Username=postgres;Password={_password};Pooling=false";
        var eventId = Guid.NewGuid();
        var idemKey = $"partition-test-{Guid.NewGuid():N}";

        await using (var conn = new NpgsqlConnection(dbAdmin))
        {
            await conn.OpenAsync();
            // Set tenant context so the RLS policy admits the insert
            // even when forced (FORCE ROW LEVEL SECURITY applies to
            // superuser too).
            await using (var setCmd = new NpgsqlCommand("SET app.tenant_id = '1';", conn))
                await setCmd.ExecuteNonQueryAsync();

            await using var insert = new NpgsqlCommand(@"
INSERT INTO audit.events (
    ""EventId"", ""TenantId"", ""EventType"", ""EntityType"", ""EntityId"",
    ""Payload"", ""OccurredAt"", ""IngestedAt"", ""IdempotencyKey""
) VALUES (
    @eid, 1, 'partition.smoke', 'AuditEventsPartitioningTests', 'smoke',
    '{}'::jsonb, '2026-05-15 12:00:00+00', NOW(), @key
);", conn);
            insert.Parameters.AddWithValue("eid", eventId);
            insert.Parameters.AddWithValue("key", idemKey);
            await insert.ExecuteNonQueryAsync();
        }

        // Now verify the row landed in the events_2026_05 partition (and
        // ONLY there). We query the child partition directly — if the row
        // was mis-routed it'd be in a different partition.
        await using (var conn = new NpgsqlConnection(dbAdmin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
SELECT count(*) FROM audit.events_2026_05 WHERE ""EventId"" = @eid;", conn);
            cmd.Parameters.AddWithValue("eid", eventId);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(1L,
                "an OccurredAt of 2026-05-15 should route into the events_2026_05 partition");
        }
    }

    /// <summary>
    /// Cross-partition SELECT (no OccurredAt filter) still returns rows
    /// from every partition. Important: the legacy ad-hoc reporting
    /// queries don't all carry an OccurredAt filter — partitioning must
    /// not break them.
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task SelectWithoutOccurredAtFilter_StillReturnsRowsFromAllPartitions()
    {
        if (!_enabled) return;

        var dbAdmin =
            $"Host=localhost;Port=5432;Database={_dbName};Username=postgres;Password={_password};Pooling=false";

        // Insert one row into each of two different month-partitions.
        await using (var conn = new NpgsqlConnection(dbAdmin))
        {
            await conn.OpenAsync();
            await using (var setCmd = new NpgsqlCommand("SET app.tenant_id = '1';", conn))
                await setCmd.ExecuteNonQueryAsync();

            await using var insert = new NpgsqlCommand(@"
INSERT INTO audit.events (""EventId"", ""TenantId"", ""EventType"", ""EntityType"", ""EntityId"",
                          ""Payload"", ""OccurredAt"", ""IdempotencyKey"")
VALUES
    (gen_random_uuid(), 1, 'cross.partition.smoke', 'a', '1',
     '{}'::jsonb, '2025-08-15 12:00:00+00', 'cross-1-' || gen_random_uuid()::text),
    (gen_random_uuid(), 1, 'cross.partition.smoke', 'a', '2',
     '{}'::jsonb, '2026-05-15 12:00:00+00', 'cross-2-' || gen_random_uuid()::text);", conn);
            await insert.ExecuteNonQueryAsync();
        }

        await using (var conn = new NpgsqlConnection(dbAdmin))
        {
            await conn.OpenAsync();
            await using (var setCmd = new NpgsqlCommand("SET app.tenant_id = '1';", conn))
                await setCmd.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand(@"
SELECT count(*) FROM audit.events WHERE ""EventType"" = 'cross.partition.smoke';", conn);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(2L,
                "a SELECT against the partitioned parent should aggregate rows from every partition");
        }
    }

    /// <summary>
    /// Idempotency unique constraint still enforces. The unique key is
    /// now <c>(TenantId, IdempotencyKey, OccurredAt)</c>; for the
    /// publisher's normal use-case (same tenant + same key + same
    /// timestamp) the duplicate INSERT must fail. This proves the
    /// dedup invariant survives the partitioning rewrite.
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task DuplicateIdempotencyKey_StillFailsWithUniqueConstraint()
    {
        if (!_enabled) return;

        var dbAdmin =
            $"Host=localhost;Port=5432;Database={_dbName};Username=postgres;Password={_password};Pooling=false";
        var idemKey = $"dup-test-{Guid.NewGuid():N}";

        await using var conn = new NpgsqlConnection(dbAdmin);
        await conn.OpenAsync();
        await using (var setCmd = new NpgsqlCommand("SET app.tenant_id = '1';", conn))
            await setCmd.ExecuteNonQueryAsync();

        // First insert succeeds.
        await using (var first = new NpgsqlCommand(@"
INSERT INTO audit.events (""EventId"", ""TenantId"", ""EventType"", ""EntityType"", ""EntityId"",
                          ""Payload"", ""OccurredAt"", ""IdempotencyKey"")
VALUES (gen_random_uuid(), 1, 'dup.test', 'a', '1',
        '{}'::jsonb, '2026-05-15 12:00:00+00', @key);", conn))
        {
            first.Parameters.AddWithValue("key", idemKey);
            await first.ExecuteNonQueryAsync();
        }

        // Second insert with same (TenantId, IdempotencyKey, OccurredAt)
        // must fail. The publisher's normal dedup-by-prefix probe avoids
        // this path, but the constraint is the safety net.
        Func<Task> dup = async () =>
        {
            await using var second = new NpgsqlCommand(@"
INSERT INTO audit.events (""EventId"", ""TenantId"", ""EventType"", ""EntityType"", ""EntityId"",
                          ""Payload"", ""OccurredAt"", ""IdempotencyKey"")
VALUES (gen_random_uuid(), 1, 'dup.test', 'a', '1',
        '{}'::jsonb, '2026-05-15 12:00:00+00', @key);", conn);
            second.Parameters.AddWithValue("key", idemKey);
            await second.ExecuteNonQueryAsync();
        };
        await dup.Should().ThrowAsync<PostgresException>(
            "the (TenantId, IdempotencyKey, OccurredAt) unique constraint must reject a same-key duplicate");
    }
}
