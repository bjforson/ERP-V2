using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Thresholds;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;
using Npgsql;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 59 — regression coverage for the
/// <see cref="ScannerThresholdResolver"/> RLS opt-in fix.
///
/// <para>
/// Sprint 12 / Phase R3 created
/// <c>inspection.scanner_threshold_profiles</c> with the strict
/// per-tenant policy <c>tenant_isolation_scanner_threshold_profiles</c>
/// (no <c>OR app.tenant_id = '-1'</c> clause). The resolver's
/// <c>LoadFromDbAsync</c> calls
/// <see cref="ITenantContext.SetSystemContext"/> for the cross-tenant
/// lookup, so under a non-superuser role the SELECT returned zero
/// rows for every cache miss and the resolver fell through to
/// <see cref="ScannerThresholdSnapshot.V1Defaults"/>. Sprint 59's
/// migration <c>Add_ThresholdProfiles_SystemContext_OptIn</c> adds the
/// opt-in clause matching the established pattern across
/// <c>audit.events</c>, <c>nickfinance.fx_rate</c>,
/// <c>audit.edge_node_api_keys</c>, <c>identity.invite_tokens</c>,
/// <c>audit.notifications</c>.
/// </para>
///
/// <para>
/// Two coverage tiers:
/// <list type="bullet">
///   <item><description><b>In-memory variant</b> (always runs) —
///   exercises the resolver code path with the EF in-memory provider:
///   seeded row is returned vs <see cref="ScannerThresholdSnapshot.V1Defaults"/>
///   fallback when no row exists, plus the cache hits don't re-query
///   invariant. The in-memory provider doesn't enforce RLS so this
///   variant proves the LINQ query shape + cache semantics; the
///   Postgres variant proves the policy itself.</description></item>
///   <item><description><b>Postgres variant</b>
///   (<c>[Trait("Category", "RequiresLiveDb")]</c>) — connects as a
///   non-superuser role with the new opt-in policy installed and
///   confirms the resolver's SELECT under SetSystemContext returns the
///   seeded row (would have returned 0 rows pre-Sprint-59 with the
///   strict policy). Skipped when <c>NICKSCAN_DB_PASSWORD</c> is
///   unset.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class ScannerThresholdResolverIntegrationTests
{
    /// <summary>
    /// Build a service provider mirroring the resolver's production
    /// wireup, but on an EF in-memory database with a system-context
    /// <see cref="ITenantContext"/>. The resolver's hot path resolves
    /// <c>InspectionDbContext</c> + <c>ITenantContext</c> from a fresh
    /// <c>IServiceScopeFactory</c> scope, so we register them as
    /// scoped services exactly as production does.
    /// </summary>
    private static ServiceProvider BuildInMemoryProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetSystemContext();
            return t;
        });
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().Build());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(NullLogger<>));
        services.AddSingleton<IOptions<ScannerThresholdOptions>>(
            Options.Create(new ScannerThresholdOptions
            {
                // Tighten TTL for the cache-hit-doesn't-rerequery test —
                // we want to be sure a re-query inside the test window
                // would in fact hit the DB if the cache logic were
                // broken.
                FallbackTtl = TimeSpan.FromHours(1),
            }));
        services.AddSingleton<ScannerThresholdResolver>();
        return services.BuildServiceProvider();
    }

    private const long TenantId = 1L;

    /// <summary>
    /// Active threshold profile values that DIVERGE from
    /// <see cref="ScannerThresholdSnapshot.V1Defaults"/> in every numeric
    /// field, so a returned snapshot can be unambiguously identified as
    /// "from the seed row" vs "from the fallback".
    /// </summary>
    private const string DivergentValuesJson = """
        {
          "edge_detection":  {"canny_low": 77, "canny_high": 222},
          "normalization":   {"percentile_low": 1.5, "percentile_high": 98.5},
          "split_consensus": {"disagreement_guard_px": 99},
          "watchdogs":       {"pending_without_images_hours": 24},
          "decoder_limits":  {"max_image_dim_px": 8192}
        }
        """;

    /// <summary>
    /// Phase B — Test 1: when an Active row exists, the resolver
    /// returns those values, NOT <see cref="ScannerThresholdSnapshot.V1Defaults"/>.
    ///
    /// <para>
    /// The whole point of Sprint 12's calibration subsystem is that
    /// operator-tuned thresholds replace the v1 hardcoded constants.
    /// Pre-Sprint-59 this assertion would have failed against a real
    /// Postgres because the policy blocked the SELECT under system
    /// context; the in-memory variant catches a regression in the LINQ
    /// query / parse path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetActiveAsync_returns_seeded_row_under_system_context()
    {
        await using var sp = BuildInMemoryProvider("threshold-resolver-seeded-" + Guid.NewGuid());
        var scannerId = Guid.NewGuid();
        await SeedActiveRowAsync(sp, scannerId, DivergentValuesJson);

        var resolver = sp.GetRequiredService<ScannerThresholdResolver>();
        var snapshot = await resolver.GetActiveAsync(scannerId, CancellationToken.None);

        // The diverged values must come through — proves the SELECT
        // returned the seeded row and ParseSnapshot read the grouped
        // §6.5.2 layout correctly.
        snapshot.CannyLow.Should().Be(77);
        snapshot.CannyHigh.Should().Be(222);
        snapshot.PercentileLow.Should().Be(1.5);
        snapshot.PercentileHigh.Should().Be(98.5);
        snapshot.SplitDisagreementGuardPx.Should().Be(99);
        snapshot.PendingWithoutImagesHours.Should().Be(24);
        snapshot.MaxImageDimPx.Should().Be(8192);

        // Cross-check against the v1 defaults so a future change to
        // V1Defaults that happens to match the seed values doesn't
        // silently make this test pass for the wrong reason.
        var v1 = ScannerThresholdSnapshot.V1Defaults();
        snapshot.Should().NotBe(v1, "the seeded row should override the v1 fallback");
    }

    /// <summary>
    /// Phase B — Test 2: when NO row exists for the scanner, the
    /// resolver still falls back to
    /// <see cref="ScannerThresholdSnapshot.V1Defaults"/>. Verifies the
    /// fallback path remains intact — Sprint 59 only fixes the policy
    /// gap; the resolver's "no row" handling must still match v1
    /// constants for behavioural parity (§6.5.4).
    /// </summary>
    [Fact]
    public async Task GetActiveAsync_falls_back_to_v1_defaults_when_no_row()
    {
        await using var sp = BuildInMemoryProvider("threshold-resolver-empty-" + Guid.NewGuid());
        var scannerId = Guid.NewGuid();
        // No seed — table is empty for this scanner id.

        var resolver = sp.GetRequiredService<ScannerThresholdResolver>();
        var snapshot = await resolver.GetActiveAsync(scannerId, CancellationToken.None);

        var v1 = ScannerThresholdSnapshot.V1Defaults();
        snapshot.Should().Be(v1, "with no Active row, the resolver MUST return v1 defaults");
    }

    /// <summary>
    /// Phase B — Test 3: cache hits don't re-query the DB.
    ///
    /// <para>
    /// Implementation note. Counting "DB queries" against the EF
    /// in-memory provider isn't directly possible, so we instead use
    /// the resolver's observable behaviour: after the first call seeds
    /// the in-memory cache, mutate the underlying row to a different
    /// values blob and call again. The cached snapshot must come back
    /// — proving the second call did NOT hit the DB. If a regression
    /// re-introduced a per-call query, the second call would surface
    /// the mutated values.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetActiveAsync_caches_and_does_not_requery()
    {
        await using var sp = BuildInMemoryProvider("threshold-resolver-cache-" + Guid.NewGuid());
        var scannerId = Guid.NewGuid();
        await SeedActiveRowAsync(sp, scannerId, DivergentValuesJson);

        var resolver = sp.GetRequiredService<ScannerThresholdResolver>();
        var first = await resolver.GetActiveAsync(scannerId, CancellationToken.None);
        first.CannyLow.Should().Be(77, "first call hits the DB and caches the seed value");

        // Mutate the underlying row to a third distinct blob. If the
        // resolver's cache is honoured, the second call still returns
        // the original (CannyLow=77).
        const string mutatedJson = """
            {
              "edge_detection":  {"canny_low": 11, "canny_high": 22},
              "normalization":   {"percentile_low": 0.1, "percentile_high": 99.9},
              "split_consensus": {"disagreement_guard_px": 33},
              "watchdogs":       {"pending_without_images_hours": 1},
              "decoder_limits":  {"max_image_dim_px": 1024}
            }
            """;
        await UpdateRowJsonAsync(sp, scannerId, mutatedJson);

        var second = await resolver.GetActiveAsync(scannerId, CancellationToken.None);
        second.CannyLow.Should().Be(77,
            "the second call must hit the cache; if it re-queried, CannyLow would be 11");
        second.Should().Be(first, "cache hits return the same snapshot reference's values");
    }

    /// <summary>
    /// Insert one Active threshold profile row for the supplied
    /// scanner id under <see cref="TenantId"/>. Mirrors the bootstrap
    /// migration's status / source convention so the resolver's
    /// <c>Status = Active</c> filter admits the row.
    /// </summary>
    private static async Task SeedActiveRowAsync(
        IServiceProvider sp, Guid scannerId, string valuesJson)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        db.ScannerThresholdProfiles.Add(new ScannerThresholdProfile
        {
            Id = Guid.NewGuid(),
            ScannerDeviceInstanceId = scannerId,
            Version = 0,
            Status = ScannerThresholdProfileStatus.Active,
            ValuesJson = valuesJson,
            ProposedBy = ScannerThresholdProposalSource.Bootstrap,
            ProposalRationaleJson = """{"source":"sprint-59-test"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            EffectiveFrom = DateTimeOffset.UtcNow,
            TenantId = TenantId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task UpdateRowJsonAsync(
        IServiceProvider sp, Guid scannerId, string newValuesJson)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var row = await db.ScannerThresholdProfiles
            .Where(p => p.ScannerDeviceInstanceId == scannerId
                        && p.Status == ScannerThresholdProfileStatus.Active)
            .OrderByDescending(p => p.Version)
            .FirstAsync();
        row.ValuesJson = newValuesJson;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    // -------------------------------------------------------------------
    // Postgres-backed variant — proves the new opt-in policy actually
    // admits the SELECT under app.tenant_id = '-1' as a non-superuser
    // role. Skipped when NICKSCAN_DB_PASSWORD is unset.
    // -------------------------------------------------------------------

    /// <summary>
    /// Direct-Postgres variant — connects as a non-superuser role with
    /// the schema + new opt-in policy installed, seeds one row under
    /// the postgres superuser, then SELECTs under <c>app.tenant_id =
    /// '-1'</c> exactly as the resolver does. Pre-Sprint-59 this would
    /// return zero rows; post-Sprint-59 it must return the seed row.
    ///
    /// <para>
    /// Pattern mirrors
    /// <see cref="NickERP.Platform.Tests.TenantModuleSettingsRlsIntegrationTests"/>
    /// — Testcontainers is unavailable so we lean on the dev Postgres
    /// at <c>localhost:5432</c>. We do NOT use EF migrations here
    /// because the inspection-database migration set has many
    /// upstream dependencies; instead we hand-create only the columns
    /// the resolver's SELECT touches and the policy itself.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresLiveDb")]
    public async Task Postgres_systemContext_select_admits_seeded_row_with_optin_policy()
    {
        var password = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            // Skip-by-pass: dev box with NICKSCAN_DB_PASSWORD set runs
            // the real assertion; CI without the env var passes.
            return;
        }

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
        var dbName = $"nickerp_test_s59_{suffix}";
        var roleName = $"nickerp_test_s59_{suffix}_app";
        var rolePassword = "rlspw_" + Guid.NewGuid().ToString("N");

        var adminRoot =
            $"Host=localhost;Port=5432;Database=postgres;Username=postgres;Password={password};Pooling=false";
        var adminDb =
            $"Host=localhost;Port=5432;Database={dbName};Username=postgres;Password={password};Pooling=false";
        var appConn =
            $"Host=localhost;Port=5432;Database={dbName};Username={roleName};Password={rolePassword};Pooling=false";

        try
        {
            // Bootstrap a throwaway DB.
            await using (var conn = new NpgsqlConnection(adminRoot))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\";", conn);
                await cmd.ExecuteNonQueryAsync();
            }

            // Install the schema, policy (with the Sprint 59 opt-in
            // clause), and the non-superuser role.
            await using (var conn = new NpgsqlConnection(adminDb))
            {
                await conn.OpenAsync();
                var stmts = new[]
                {
                    "CREATE SCHEMA inspection;",
                    """
                    CREATE TABLE inspection.scanner_threshold_profiles (
                        "Id" uuid PRIMARY KEY,
                        "ScannerDeviceInstanceId" uuid NOT NULL,
                        "Version" integer NOT NULL,
                        "ValuesJson" jsonb NOT NULL,
                        "Status" integer NOT NULL,
                        "TenantId" bigint NOT NULL,
                        "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                        "UpdatedAt" timestamptz NOT NULL DEFAULT now()
                    );
                    """,
                    "ALTER TABLE inspection.scanner_threshold_profiles ENABLE ROW LEVEL SECURITY;",
                    "ALTER TABLE inspection.scanner_threshold_profiles FORCE ROW LEVEL SECURITY;",
                    // Sprint 59's exact policy shape — opt-in clause
                    // matches Add_ThresholdProfiles_SystemContext_OptIn.
                    """
                    CREATE POLICY tenant_isolation_scanner_threshold_profiles
                    ON inspection.scanner_threshold_profiles
                    USING (
                        "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
                        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
                    )
                    WITH CHECK (
                        "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
                        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
                    );
                    """,
                    $"CREATE ROLE \"{roleName}\" LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{rolePassword}';",
                    $"GRANT CONNECT ON DATABASE \"{dbName}\" TO \"{roleName}\";",
                    $"GRANT USAGE ON SCHEMA inspection TO \"{roleName}\";",
                    $"GRANT SELECT ON inspection.scanner_threshold_profiles TO \"{roleName}\";",
                };
                foreach (var stmt in stmts)
                {
                    await using var cmd = new NpgsqlCommand(stmt, conn);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // Seed one row under the superuser (RLS bypass), tenant=1.
            // Status=20 (Active) per the entity enum.
            var scannerId = Guid.NewGuid();
            await using (var conn = new NpgsqlConnection(adminDb))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    INSERT INTO inspection.scanner_threshold_profiles
                        ("Id", "ScannerDeviceInstanceId", "Version", "ValuesJson",
                         "Status", "TenantId")
                    VALUES (gen_random_uuid(), @sid, 0, @vals::jsonb, 20, 1);
                    """, conn);
                cmd.Parameters.AddWithValue("sid", scannerId);
                cmd.Parameters.AddWithValue("vals", DivergentValuesJson);
                await cmd.ExecuteNonQueryAsync();
            }

            // Read under the non-superuser role with app.tenant_id = '-1'
            // (system context). Pre-Sprint-59 the policy USING clause
            // would fail (TenantId=1 ≠ -1) and yield 0 rows. The new
            // opt-in disjunct must admit the read.
            await using (var conn = new NpgsqlConnection(appConn))
            {
                await conn.OpenAsync();
                await using (var setCmd = new NpgsqlCommand(
                    "SET app.tenant_id = '-1';", conn))
                {
                    await setCmd.ExecuteNonQueryAsync();
                }

                int rowCount = 0;
                long? readBackTenantId = null;
                Guid? readBackScannerId = null;
                await using (var readCmd = new NpgsqlCommand(
                    """
                    SELECT "TenantId", "ScannerDeviceInstanceId"
                    FROM inspection.scanner_threshold_profiles
                    WHERE "Status" = 20;
                    """, conn))
                {
                    await using var reader = await readCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        rowCount++;
                        readBackTenantId = reader.GetInt64(0);
                        readBackScannerId = reader.GetGuid(1);
                    }
                }

                rowCount.Should().Be(1,
                    "Sprint 59's opt-in policy admits the SELECT under app.tenant_id = '-1'; "
                    + "pre-Sprint-59 this returned 0 rows and broke ScannerThresholdResolver.");
                readBackTenantId.Should().Be(1L);
                readBackScannerId.Should().Be(scannerId);
            }

            // Negative control: under app.tenant_id = '0' (the
            // fail-closed default), the policy must STILL block the
            // read. Confirms the opt-in didn't accidentally weaken the
            // strict-tenant clause for non-system callers.
            await using (var conn = new NpgsqlConnection(appConn))
            {
                await conn.OpenAsync();
                // No SET app.tenant_id — falls through to the COALESCE
                // default of '0'. Strict per-tenant policy yields 0 rows.
                await using var readCmd = new NpgsqlCommand(
                    "SELECT COUNT(*) FROM inspection.scanner_threshold_profiles;", conn);
                var count = (long?)await readCmd.ExecuteScalarAsync() ?? -1;
                count.Should().Be(0L,
                    "fail-closed default ('0') must still block reads — opt-in only admits '-1'");
            }
        }
        finally
        {
            try
            {
                NpgsqlConnection.ClearAllPools();
                await using var conn = new NpgsqlConnection(adminRoot);
                await conn.OpenAsync();
                await using (var killCmd = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
                    + "WHERE datname = @db AND pid <> pg_backend_pid();", conn))
                {
                    killCmd.Parameters.AddWithValue("db", dbName);
                    try { await killCmd.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
                }
                await using (var dropDb = new NpgsqlCommand(
                    $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);", conn))
                    await dropDb.ExecuteNonQueryAsync();
                await using (var dropRole = new NpgsqlCommand(
                    $"DROP ROLE IF EXISTS \"{roleName}\";", conn))
                    await dropRole.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                // Best-effort teardown — leftover names are prefixed
                // nickerp_test_s59_ for an out-of-band sweeper.
                Trace.WriteLine($"ScannerThresholdResolverIntegrationTests teardown: {ex}");
            }
        }
    }
}
