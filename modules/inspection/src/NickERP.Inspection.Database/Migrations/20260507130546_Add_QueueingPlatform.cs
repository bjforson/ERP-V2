using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 14 / B-queues — combines what was originally split across
    /// Add_QueueSplitDetection (the per-module work-queue table) and
    /// Add_WorkItems (work_items + work_item_transitions). Regenerated as a
    /// single migration so EF gets a clean Designer + ModelSnapshot.
    /// EF auto-generated the work_items + work_item_transitions tables;
    /// raw migrationBuilder.Sql() blocks below add the things EF can't:
    /// the queue_split_detection table (not an EF entity — accessed via
    /// raw SQL by PostgresQueue&lt;T&gt;), the COALESCE-default on tenant_id
    /// columns, partial indexes on the queue table, and the RLS policies.
    /// </summary>
    public partial class Add_QueueingPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "work_item_transitions",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromState = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ToState = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_item_transitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "work_items",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Workflow = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CurrentState = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    IdempotencyAnchor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_items", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_item_transitions_tenant",
                schema: "inspection",
                table: "work_item_transitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_work_item_transitions_tenant_workitem_time",
                schema: "inspection",
                table: "work_item_transitions",
                columns: new[] { "TenantId", "WorkItemId", "OccurredAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_work_items_tenant",
                schema: "inspection",
                table: "work_items",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_work_items_tenant_case",
                schema: "inspection",
                table: "work_items",
                columns: new[] { "TenantId", "CaseId" });

            // ----------------------------------------------------------------
            // COALESCE-default for tenant_id columns. EF Core emits the column
            // as nullable: false without a defaultValueSql; we want the v2
            // platform's standard fail-closed default (resolves from the
            // session-set app.tenant_id, fallback to '0' which RLS rejects).
            // ----------------------------------------------------------------
            migrationBuilder.Sql(
                "ALTER TABLE inspection.work_items "
                + "ALTER COLUMN \"TenantId\" SET DEFAULT COALESCE(current_setting('app.tenant_id', true), '0')::bigint;");
            migrationBuilder.Sql(
                "ALTER TABLE inspection.work_item_transitions "
                + "ALTER COLUMN \"TenantId\" SET DEFAULT COALESCE(current_setting('app.tenant_id', true), '0')::bigint;");

            // ----------------------------------------------------------------
            // queue_split_detection — the per-module work-queue. NOT an EF
            // entity (PostgresQueue<TPayload> hits this via raw Npgsql for
            // the FOR UPDATE SKIP LOCKED claim path), so EF can't generate
            // it via CreateTable. Schema mirrors the platform's
            // queueing.outbox + dead_letter shape.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(@"
                CREATE TABLE inspection.queue_split_detection (
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
                    ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT now()
                );");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_queue_split_detection_idempotency_key "
                + "ON inspection.queue_split_detection (\"IdempotencyKey\");");

            migrationBuilder.Sql(
                "CREATE INDEX ix_queue_split_detection_available_unclaimed "
                + "ON inspection.queue_split_detection (\"AvailableAt\") "
                + "WHERE \"ClaimedBy\" IS NULL;");

            migrationBuilder.Sql(
                "CREATE INDEX ix_queue_split_detection_work_item_id "
                + "ON inspection.queue_split_detection (\"WorkItemId\");");

            migrationBuilder.Sql(
                "CREATE INDEX ix_queue_split_detection_claimed_until "
                + "ON inspection.queue_split_detection (\"ClaimedUntil\") "
                + "WHERE \"ClaimedBy\" IS NOT NULL;");

            // ----------------------------------------------------------------
            // RLS — fail-closed tenant isolation on all three tables. Pattern
            // matches inspection schema's other tenant-owned tables (see
            // Add_RLS_Policies for the convention).
            // ----------------------------------------------------------------
            foreach (var table in new[] { "queue_split_detection", "work_items", "work_item_transitions" })
            {
                migrationBuilder.Sql($"ALTER TABLE inspection.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE inspection.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation_{table} "
                    + $"ON inspection.{table} "
                    + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                    + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Reverse order: RLS off, then queue_split_detection, then EF tables.
            foreach (var table in new[] { "work_item_transitions", "work_items", "queue_split_detection" })
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation_{table} ON inspection.{table};");
                migrationBuilder.Sql($"ALTER TABLE inspection.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE inspection.{table} DISABLE ROW LEVEL SECURITY;");
            }

            migrationBuilder.Sql("DROP INDEX IF EXISTS inspection.ix_queue_split_detection_claimed_until;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS inspection.ix_queue_split_detection_work_item_id;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS inspection.ix_queue_split_detection_available_unclaimed;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS inspection.ux_queue_split_detection_idempotency_key;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS inspection.queue_split_detection;");

            migrationBuilder.DropTable(
                name: "work_item_transitions",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "work_items",
                schema: "inspection");
        }
    }
}
