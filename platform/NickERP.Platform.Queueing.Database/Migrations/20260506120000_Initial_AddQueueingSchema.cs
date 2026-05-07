using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Queueing.Database.Migrations
{
    /// <summary>
    /// Sprint 14 / B-queues — initial schema for the platform-wide queueing
    /// subsystem. Creates the <c>queueing</c> schema and two cross-module
    /// tables: <c>queueing.outbox</c> for outbound integrations and
    /// <c>queueing.dead_letter</c> for poison messages. Per-module queue
    /// tables (<c>inspection.queue_split_detection</c>,
    /// <c>inspection.queue_audit_review</c>, etc.) live in their owning
    /// module's <c>*.Database</c> migrations and are added in subsequent
    /// sprints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Schema choice.</b> Lives in the <c>nickerp_platform</c> Postgres
    /// database alongside <c>tenancy</c>, <c>identity</c>, and
    /// <c>audit</c>. Using a dedicated <c>queueing</c> schema keeps the
    /// platform tables separate from module data and lets the
    /// <c>nscim_app</c> grants follow the same per-schema pattern the
    /// other platforms use.
    /// </para>
    /// <para>
    /// <b>Raw SQL via <see cref="MigrationBuilder.Sql(string)"/></b> for the
    /// table DDL. The columns, indexes, partial filters, materialised
    /// view, and concurrent-refresh unique index all express patterns
    /// (jsonb defaults, <c>CREATE MATERIALIZED VIEW</c>, partial indexes
    /// on <c>WHERE claimed_by IS NULL</c>) that the EF fluent API can't
    /// emit cleanly. The DbContext's <c>OnModelCreating</c> mirrors
    /// these properties for the EF-side <c>SaveChanges</c> path on the
    /// rare cases the platform writes through EF rather than the raw
    /// Npgsql claim path.
    /// </para>
    /// <para>
    /// <b>RLS policies.</b> Enabled here on both tables — every row is
    /// tenant-scoped so cross-tenant reads must be impossible at the DB
    /// layer (defence in depth: even if a worker forgets a WHERE clause
    /// or a future EF query filter regresses, RLS catches it). Pattern
    /// matches the standard <c>tenant_isolation_*</c> policy used by
    /// the other platform schemas. System context (<c>app.tenant_id =
    /// '-1'</c>) opt-in is added in a future migration when the DLQ
    /// admin UI lands; for now neither table allows cross-tenant
    /// access.
    /// </para>
    /// </remarks>
    public partial class Initial_AddQueueingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS queueing;");

            // Cross-system outbound message queue.
            migrationBuilder.Sql(@"
                CREATE TABLE queueing.outbox (
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
                );");

            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ux_outbox_idempotency_key
                    ON queueing.outbox (""IdempotencyKey"");");

            migrationBuilder.Sql(@"
                CREATE INDEX ix_outbox_available_unclaimed
                    ON queueing.outbox (""AvailableAt"")
                    WHERE ""ClaimedBy"" IS NULL;");

            migrationBuilder.Sql(@"
                CREATE INDEX ix_outbox_work_item_id
                    ON queueing.outbox (""WorkItemId"");");

            migrationBuilder.Sql(@"
                CREATE INDEX ix_outbox_claimed_until
                    ON queueing.outbox (""ClaimedUntil"")
                    WHERE ""ClaimedBy"" IS NOT NULL;");

            migrationBuilder.Sql(@"
                CREATE INDEX ix_outbox_kind_availableat
                    ON queueing.outbox (""DestinationKind"", ""AvailableAt"");");

            // Promoted-failure dead-letter queue.
            migrationBuilder.Sql(@"
                CREATE TABLE queueing.dead_letter (
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
                );");

            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ux_dead_letter_idempotency_key
                    ON queueing.dead_letter (""IdempotencyKey"");");

            migrationBuilder.Sql(@"
                CREATE INDEX ix_dead_letter_work_item_id
                    ON queueing.dead_letter (""WorkItemId"");");

            migrationBuilder.Sql(@"
                CREATE INDEX ix_dead_letter_sourcequeue_promotedat
                    ON queueing.dead_letter (""SourceQueue"", ""PromotedAt"" DESC);");

            // queue_metrics materialised view — consolidated depth /
            // age / DLQ stats across the platform-wide tables. Per-module
            // queues extend this UNION ALL via additional migrations in
            // their own *.Database projects when they ship.
            // FILTER must attach to the aggregate (MIN) directly, not to the wrapping EXTRACT —
            // Postgres parser rejects EXTRACT(...) FILTER (...) since EXTRACT isn't itself an
            // aggregate. Sliding the FILTER inside the parens fixes the syntax.
            migrationBuilder.Sql(@"
                CREATE MATERIALIZED VIEW queueing.queue_metrics AS
                SELECT
                    'queueing.outbox'::varchar(128)         AS queue_name,
                    COUNT(*) FILTER (WHERE ""ClaimedBy"" IS NULL AND ""AvailableAt"" <= now())
                                                            AS depth,
                    COALESCE(EXTRACT(EPOCH FROM (
                                now() - MIN(""AvailableAt"") FILTER (WHERE ""ClaimedBy"" IS NULL AND ""AvailableAt"" <= now())
                            )), 0)::bigint                  AS oldest_age_seconds,
                    COUNT(*) FILTER (WHERE ""ClaimedBy"" IS NOT NULL)
                                                            AS in_flight,
                    (SELECT COUNT(*) FROM queueing.dead_letter WHERE ""SourceQueue"" = 'queueing.outbox')
                                                            AS dlq_depth,
                    now()                                   AS refreshed_at
                FROM queueing.outbox
                UNION ALL
                SELECT
                    'queueing.dead_letter'::varchar(128),
                    COUNT(*) FILTER (WHERE ""ClaimedBy"" IS NULL AND ""AvailableAt"" <= now()),
                    COALESCE(EXTRACT(EPOCH FROM (
                                now() - MIN(""AvailableAt"") FILTER (WHERE ""ClaimedBy"" IS NULL AND ""AvailableAt"" <= now())
                            )), 0)::bigint,
                    COUNT(*) FILTER (WHERE ""ClaimedBy"" IS NOT NULL),
                    0,
                    now()
                FROM queueing.dead_letter;");

            // Unique index on queue_name — required for
            // REFRESH MATERIALIZED VIEW CONCURRENTLY (which reads
            // a unique key to incrementally diff).
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ux_queue_metrics_queue_name
                    ON queueing.queue_metrics (queue_name);");

            // RLS policies — fail-closed default tenant '0' matches
            // the rest of the platform.
            foreach (var table in new[] { "outbox", "dead_letter" })
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE queueing.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"ALTER TABLE queueing.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation_{table} ON queueing.{table} "
                    + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                    + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "outbox", "dead_letter" })
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation_{table} ON queueing.{table};");
                migrationBuilder.Sql($"ALTER TABLE queueing.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE queueing.{table} DISABLE ROW LEVEL SECURITY;");
            }
            migrationBuilder.Sql("DROP MATERIALIZED VIEW IF EXISTS queueing.queue_metrics;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS queueing.dead_letter;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS queueing.outbox;");
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS queueing;");
        }
    }
}
