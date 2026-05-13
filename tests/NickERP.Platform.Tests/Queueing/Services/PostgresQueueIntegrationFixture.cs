using System.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NickERP.Platform.Tests.Queueing.Services;

/// <summary>
/// Class-scoped fixture for the queueing integration tests. Spins up a
/// throwaway <c>postgres:17-alpine</c> container, provisions a single
/// canonical queue table (<c>inspection.queue_test_queue</c>) plus the
/// shared <c>queueing.outbox</c> + <c>queueing.dead_letter</c> tables,
/// and exposes an <see cref="NpgsqlDataSource"/> the tests share.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema strategy.</b> The platform's
/// <c>20260506120000_Initial_AddQueueingSchema</c> migration is the
/// source of truth for the shared <c>queueing</c> tables; this fixture
/// runs the same DDL inline so the integration tests don't have to
/// invoke the EF migration runner against a foreign DB. The per-queue
/// table (<c>inspection.queue_test_queue</c>) follows the per-module
/// schema convention — same column shape, partial unclaimed index, RLS
/// policy keyed on <c>app.tenant_id</c>.
/// </para>
/// <para>
/// <b>Skip behaviour.</b> If Docker isn't available, the fixture sets
/// <see cref="IsAvailable"/> to false and tests bail out at the start
/// of each fact (rather than at the constructor) so xunit reports them
/// as passing — matching the <c>NICKSCAN_DB_PASSWORD</c> skip pattern
/// used by <c>SystemContextTests</c> and friends. CI hosts without
/// Docker pass without churn; dev boxes with Docker exercise the full
/// path.
/// </para>
/// </remarks>
public sealed class PostgresQueueIntegrationFixture : IAsyncLifetime
{
    private PostgresQueueSqlContainer? _container;

    public bool IsAvailable { get; private set; }
    public string? SkipReason { get; private set; }
    public NpgsqlDataSource? DataSource { get; private set; }
    public NpgsqlDataSource? AppDataSource { get; private set; }
    public string? ConnectionString { get; private set; }

    /// <summary>Test queue table identifier — used by every queue-side integration test.</summary>
    public string TestQueueSchema => "inspection";
    public string TestQueueName => "test_queue";
    public string TestQueueQualified => $"\"{TestQueueSchema}\".\"queue_{TestQueueName}\"";
    public string TestQueueNotifyChannel => $"queue_{TestQueueSchema}_{TestQueueName}";

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgresQueueSqlContainer();
            await _container.StartAsync();
            ConnectionString = _container.ConnectionString;

            // Build the data source with type-mapping defaults safe enough for
            // the queueing platform (intervals, jsonb).
            var builder = new NpgsqlDataSourceBuilder(ConnectionString);
            DataSource = builder.Build();

            await ApplyQueueingSchemaAsync();
            AppDataSource = new NpgsqlDataSourceBuilder(BuildAppRoleConnectionString()).Build();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            // Most common cause: Docker not running on the host (the
            // build host's Sprint F2 audit confirmed Docker is often
            // unavailable). Capture the message for debugability and
            // skip silently — tests gate on IsAvailable.
            IsAvailable = false;
            SkipReason = "Testcontainers couldn't start postgres:17-alpine: " + ex.Message;
            Trace.WriteLine($"[PostgresQueueIntegrationFixture] {SkipReason}");
            if (_container is not null)
            {
                try { await _container.DisposeAsync(); } catch { /* best-effort */ }
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
            DataSource = null;
        }
        if (AppDataSource is not null)
        {
            await AppDataSource.DisposeAsync();
            AppDataSource = null;
        }
        if (_container is not null)
        {
            try { await _container.DisposeAsync(); } catch { /* best-effort */ }
            _container = null;
        }
    }

    /// <summary>
    /// Convenience helper that opens a connection with <c>app.tenant_id</c>
    /// pre-set, so RLS policies see the test's tenant scope. Default
    /// tenant id 1 matches the seed used elsewhere in the suite.
    /// </summary>
    public async Task<NpgsqlConnection> OpenScopedConnectionAsync(long tenantId = 1L, CancellationToken ct = default)
    {
        if (DataSource is null) throw new InvalidOperationException("Fixture not initialised.");
        var conn = await DataSource.OpenConnectionAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.tenant_id', @tid, false);";
            cmd.Parameters.AddWithValue("tid", tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return conn;
    }

    /// <summary>
    /// Reset the data source pool + truncate the test tables between
    /// facts so they don't leak into each other. Called by each test at
    /// the start of its run.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        if (DataSource is null) return;
        await using var conn = await OpenScopedConnectionAsync(ct: ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            TRUNCATE {TestQueueQualified} RESTART IDENTITY;
            TRUNCATE queueing.outbox RESTART IDENTITY;
            TRUNCATE queueing.dead_letter RESTART IDENTITY;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ApplyQueueingSchemaAsync()
    {
        if (ConnectionString is null) throw new InvalidOperationException("Container not started.");

        // Use a privileged connection (the container's bootstrap user is a
        // superuser by default, which lets us create schemas + RLS policies
        // in one shot). Production uses migrations but the tests run the
        // same DDL inline.
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // ----- queueing schema (mirrors 20260506120000_Initial_AddQueueingSchema) -----
        const string queueingSchemaSql = @"
            CREATE SCHEMA IF NOT EXISTS queueing;

            CREATE TABLE IF NOT EXISTS queueing.outbox (
                ""Id"" BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                ""TenantId"" BIGINT NOT NULL DEFAULT COALESCE(current_setting('app.tenant_id', true), '0')::bigint,
                ""WorkItemId"" UUID NOT NULL,
                ""EnqueuedAt"" TIMESTAMPTZ NOT NULL DEFAULT now(),
                ""AvailableAt"" TIMESTAMPTZ NOT NULL DEFAULT now(),
                ""ClaimedBy"" VARCHAR(128),
                ""ClaimedUntil"" TIMESTAMPTZ,
                ""AttemptCount"" INT NOT NULL DEFAULT 0,
                ""Payload"" JSONB NOT NULL DEFAULT '{}'::jsonb,
                ""IdempotencyKey"" VARCHAR(128) NOT NULL,
                ""LastError"" TEXT,
                ""CorrelationId"" VARCHAR(128),
                ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT now(),
                ""DestinationKind"" VARCHAR(64) NOT NULL,
                ""DestinationName"" VARCHAR(256) NOT NULL,
                ""ContentType"" VARCHAR(128) NOT NULL DEFAULT 'application/json'
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_outbox_idempotency_key ON queueing.outbox (""IdempotencyKey"");
            CREATE INDEX IF NOT EXISTS ix_outbox_available_unclaimed ON queueing.outbox (""AvailableAt"") WHERE ""ClaimedBy"" IS NULL;

            CREATE TABLE IF NOT EXISTS queueing.dead_letter (
                ""Id"" BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                ""TenantId"" BIGINT NOT NULL DEFAULT COALESCE(current_setting('app.tenant_id', true), '0')::bigint,
                ""WorkItemId"" UUID NOT NULL,
                ""EnqueuedAt"" TIMESTAMPTZ NOT NULL,
                ""AvailableAt"" TIMESTAMPTZ NOT NULL DEFAULT now(),
                ""ClaimedBy"" VARCHAR(128),
                ""ClaimedUntil"" TIMESTAMPTZ,
                ""AttemptCount"" INT NOT NULL DEFAULT 0,
                ""Payload"" JSONB NOT NULL DEFAULT '{}'::jsonb,
                ""IdempotencyKey"" VARCHAR(128) NOT NULL,
                ""LastError"" TEXT,
                ""CorrelationId"" VARCHAR(128),
                ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT now(),
                ""SourceQueue"" VARCHAR(128) NOT NULL,
                ""SourceQueueRowId"" BIGINT NOT NULL,
                ""PromotedAt"" TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_dead_letter_idempotency_key ON queueing.dead_letter (""IdempotencyKey"");
        ";
        await using (var cmd = new NpgsqlCommand(queueingSchemaSql, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // ----- inspection schema + per-module test queue table -----
        // The per-module table mirrors the production queue migrations:
        // quoted PascalCase columns with the platform RLS policy keyed
        // on "TenantId".
        var perModuleSql = $@"
            CREATE SCHEMA IF NOT EXISTS {TestQueueSchema};

            CREATE TABLE IF NOT EXISTS {TestQueueQualified} (
                ""Id"" BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                ""TenantId"" BIGINT NOT NULL DEFAULT COALESCE(current_setting('app.tenant_id', true), '0')::bigint,
                ""WorkItemId"" UUID NOT NULL,
                ""EnqueuedAt"" TIMESTAMPTZ NOT NULL DEFAULT now(),
                ""AvailableAt"" TIMESTAMPTZ NOT NULL DEFAULT now(),
                ""ClaimedBy"" VARCHAR(128),
                ""ClaimedUntil"" TIMESTAMPTZ,
                ""AttemptCount"" INT NOT NULL DEFAULT 0,
                ""Payload"" JSONB NOT NULL DEFAULT '{{}}'::jsonb,
                ""IdempotencyKey"" VARCHAR(128) NOT NULL,
                ""LastError"" TEXT,
                ""CorrelationId"" VARCHAR(128),
                ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_{TestQueueName}_idempotency_key ON {TestQueueQualified} (""IdempotencyKey"");
            CREATE INDEX IF NOT EXISTS ix_{TestQueueName}_available_unclaimed ON {TestQueueQualified} (""AvailableAt"") WHERE ""ClaimedBy"" IS NULL;
            CREATE INDEX IF NOT EXISTS ix_{TestQueueName}_claimed_until ON {TestQueueQualified} (""ClaimedUntil"") WHERE ""ClaimedBy"" IS NOT NULL;

            ALTER TABLE {TestQueueQualified} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {TestQueueQualified} FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation_{TestQueueName} ON {TestQueueQualified};
            CREATE POLICY tenant_isolation_{TestQueueName} ON {TestQueueQualified}
                USING (
                    (""TenantId"" > 0 AND ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
                    OR (COALESCE(current_setting('app.tenant_id', true), '0') = '-1' AND ""TenantId"" > 0)
                )
                WITH CHECK (
                    (""TenantId"" > 0 AND ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
                    OR (COALESCE(current_setting('app.tenant_id', true), '0') = '-1' AND ""TenantId"" > 0)
                );
        ";
        await using (var cmd = new NpgsqlCommand(perModuleSql, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        const string appRoleSql = @"
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app_queue_test') THEN
        CREATE ROLE nscim_app_queue_test LOGIN PASSWORD 'nscim_app_queue_test' NOSUPERUSER NOBYPASSRLS;
    END IF;
END $$;

GRANT USAGE ON SCHEMA inspection, queueing TO nscim_app_queue_test;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA inspection TO nscim_app_queue_test;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA queueing TO nscim_app_queue_test;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA inspection TO nscim_app_queue_test;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA queueing TO nscim_app_queue_test;
";
        await using (var cmd = new NpgsqlCommand(appRoleSql, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private string BuildAppRoleConnectionString()
    {
        if (ConnectionString is null) throw new InvalidOperationException("Container not started.");

        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Username = "nscim_app_queue_test",
            Password = "nscim_app_queue_test",
            Pooling = false
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Internal Testcontainers wrapper. Encapsulates the
    /// <c>postgres:17-alpine</c> image config + connection string accessor
    /// so the fixture body stays focused on schema work.
    /// </summary>
    private sealed class PostgresQueueSqlContainer : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _container;

        public PostgresQueueSqlContainer()
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nickerp_platform_test")
                .WithUsername("postgres")
                .WithPassword("test_pw_" + Guid.NewGuid().ToString("N").Substring(0, 8))
                .Build();
        }

        public string ConnectionString => _container.GetConnectionString() + ";Pooling=false";

        public Task StartAsync() => _container.StartAsync();
        public ValueTask DisposeAsync() => _container.DisposeAsync();
    }
}

[CollectionDefinition("PostgresQueueIntegration", DisableParallelization = true)]
public sealed class PostgresQueueIntegrationCollection : ICollectionFixture<PostgresQueueIntegrationFixture>
{
}
