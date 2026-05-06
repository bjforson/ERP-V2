using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Sprint 52 / FU-audit-events-partitioning — convert the
    /// <c>audit.events</c> table to a Postgres 17 native range-partitioned
    /// table keyed on <c>OccurredAt</c>.
    ///
    /// <para>
    /// <b>Why.</b> The audit log grows monotonically; under pilot load
    /// (ROADMAP §1 / docs/perf/test-plan.md §4) the table is the largest
    /// hot-path write target in the platform DB. With ~125 cases/h at
    /// Takoradi-shaped peak and ~6 events emitted per case workflow,
    /// audit.events accumulates ~1k rows/h ≈ 8M rows/yr just from
    /// inspection traffic; multi-tenant pilot expansion easily 5-10x's
    /// that. A single un-partitioned heap with five composite indexes
    /// becomes a vacuum + index-bloat hotspot well inside a year. Native
    /// partitioning lets us prune by month at query time and DETACH /
    /// DROP whole month-partitions when retention windows close
    /// (compliance: 7-year window for finance audit; inspection events
    /// can age out earlier).
    /// </para>
    ///
    /// <para>
    /// <b>Heavy migration.</b> This rewrites every row in
    /// <c>audit.events</c> via INSERT-SELECT into the new partitioned
    /// shape. <b>Run during a deploy window.</b> Wall-clock cost scales
    /// with current row count — for a fresh dev DB it's milliseconds; on
    /// a pilot DB with months of accumulated audit history it's
    /// minutes-to-tens-of-minutes. Document the run in the deploy
    /// postmortem (CHANGELOG.md). Operator order:
    /// </para>
    ///
    /// <list type="number">
    ///   <item>Stop all writers to <c>nickerp_platform</c> (every API host).</item>
    ///   <item>Take a pgbackrest full backup
    ///         (runbook 10 §5.4) — defence-in-depth before a structural rewrite.</item>
    ///   <item>Apply this migration via <c>dotnet ef database update</c>
    ///         or <c>tools/migration-runner</c>. Watch the log for the
    ///         row-count line emitted by the INSERT-SELECT.</item>
    ///   <item>Re-start writers; spot-check
    ///         <c>SELECT count(*) FROM audit.events</c> matches the
    ///         pre-migration count.</item>
    ///   <item>Wire <c>tools/migrations/audit-events-create-partition.sql</c>
    ///         into the monthly cron / scheduled task per runbook 10 §6
    ///         (operator step; new partitions need to land before the
    ///         month they cover).</item>
    /// </list>
    ///
    /// <para>
    /// <b>What changes structurally:</b>
    /// </para>
    /// <list type="bullet">
    ///   <item>Existing <c>audit.events</c> renames to <c>audit.events_legacy_v1</c>.</item>
    ///   <item>New <c>audit.events</c> created as <c>PARTITION BY RANGE ("OccurredAt")</c>.</item>
    ///   <item>Primary key becomes composite <c>(EventId, OccurredAt)</c>
    ///         (Postgres requires the partition column in every PK / UNIQUE
    ///         constraint).</item>
    ///   <item>Idempotency unique constraint becomes
    ///         <c>(TenantId, IdempotencyKey, OccurredAt)</c> — same prefix
    ///         used by <see cref="DbEventPublisher.PublishAsync"/>, so probe
    ///         performance is unchanged.</item>
    ///   <item>FK from <c>audit.notifications.EventId</c> to
    ///         <c>audit.events.EventId</c> is dropped — Postgres cannot
    ///         FK-reference a single column of a composite PK on a partitioned
    ///         parent. Audit is append-only so the cascade-delete behaviour
    ///         never fired; the projector treats EventId as a soft handle.</item>
    ///   <item>Eighteen monthly partitions pre-created:
    ///         <c>events_2025_05</c> .. <c>events_2026_11</c> (12 back, 6 ahead
    ///         from the 2026-05-05 cut-over). Operator wires the recurring
    ///         "next month" partition step via the SQL helper.</item>
    ///   <item>RLS + role grants re-applied to the new parent. Per-partition
    ///         RLS is inherited from the parent in Postgres 17 — the
    ///         <c>tenant_isolation_events</c> policy applies through the
    ///         partition routing transparently.</item>
    ///   <item><c>audit.events_legacy_v1</c> is <b>dropped</b> at the end of
    ///         the Up() — rows have been copied; the legacy heap is no
    ///         longer authoritative. (Operator who wants a safety net should
    ///         take the §5.4 backup first.)</item>
    /// </list>
    ///
    /// <para>
    /// <b>Down.</b> Reverses to the un-partitioned shape, copying rows back.
    /// Same heavy-migration caveat. Down is provided for completeness but
    /// has not been exercised against a production-sized table — prefer a
    /// PITR restore (runbook 10 §7) over a Down rollback for live data.
    /// </para>
    /// </summary>
    public partial class Convert_AuditEvents_To_Partitioned_Table : Migration
    {
        /// <summary>
        /// First month covered by the pre-created partitions. Range partitions
        /// are inclusive-lower / exclusive-upper, so <c>events_2025_05</c>
        /// holds rows where <c>OccurredAt &gt;= 2025-05-01 AND OccurredAt &lt; 2025-06-01</c>.
        /// 12 months of back-coverage handles the in-place INSERT-SELECT for
        /// dev and pilot DBs without rejection.
        /// </summary>
        private static readonly DateTime FirstPartitionMonth = new DateTime(2025, 05, 01, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Inclusive count of pre-created partitions. 18 = 12 back + 6 ahead
        /// from the 2026-05-05 cut-over. Operators add new ones via
        /// <c>tools/migrations/audit-events-create-partition.sql</c>.
        /// </summary>
        private const int PreCreatedPartitionCount = 18;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----------------------------------------------------------------
            // 1. Drop the FK that audit.notifications has on audit.events.
            //    The composite-PK + partitioned-parent combination forbids
            //    referencing a single column of the new PK.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(@"
ALTER TABLE audit.notifications
    DROP CONSTRAINT IF EXISTS ""FK_notifications_events_EventId"";");

            // ----------------------------------------------------------------
            // 2. Rename existing audit.events out of the way; we'll recreate
            //    the name as a partitioned table and copy rows back in.
            //
            //    Note: ALTER TABLE RENAME does NOT rename the table's
            //    constraints (PK, indexes). The new partitioned table
            //    declares CONSTRAINT "PK_events" — that name would clash
            //    with the legacy PK still attached to events_legacy_v1.
            //    Explicitly rename the PK + the indexes to release the
            //    canonical names before step 3 creates them fresh.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(@"
ALTER TABLE audit.events
    RENAME TO events_legacy_v1;");

            migrationBuilder.Sql(@"
ALTER TABLE audit.events_legacy_v1
    RENAME CONSTRAINT ""PK_events"" TO ""PK_events_legacy_v1"";");

            migrationBuilder.Sql(@"
ALTER INDEX IF EXISTS audit.ux_audit_events_tenant_idempotency
    RENAME TO ux_audit_events_tenant_idempotency_legacy_v1;");
            migrationBuilder.Sql(@"
ALTER INDEX IF EXISTS audit.ix_audit_events_entity_time
    RENAME TO ix_audit_events_entity_time_legacy_v1;");
            migrationBuilder.Sql(@"
ALTER INDEX IF EXISTS audit.ix_audit_events_type_time
    RENAME TO ix_audit_events_type_time_legacy_v1;");
            migrationBuilder.Sql(@"
ALTER INDEX IF EXISTS audit.ix_audit_events_actor_time
    RENAME TO ix_audit_events_actor_time_legacy_v1;");
            migrationBuilder.Sql(@"
ALTER INDEX IF EXISTS audit.ix_audit_events_correlation
    RENAME TO ix_audit_events_correlation_legacy_v1;");
            migrationBuilder.Sql(@"
ALTER INDEX IF EXISTS audit.ix_audit_events_system_type_time
    RENAME TO ix_audit_events_system_type_time_legacy_v1;");

            // RLS state on the renamed table is no longer relevant — the
            // copy-out path runs as superuser (postgres) which BYPASSRLS by
            // role default.

            // ----------------------------------------------------------------
            // 3. Create the new partitioned parent. Schema is identical to the
            //    legacy table EXCEPT for the composite primary key which now
            //    includes OccurredAt (Postgres 17 partitioning rule). We list
            //    the columns explicitly rather than CREATE TABLE LIKE because
            //    the partitioning clause requires a fresh declaration.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE audit.events (
    ""EventId"" uuid NOT NULL DEFAULT gen_random_uuid(),
    ""TenantId"" bigint NULL,
    ""ActorUserId"" uuid NULL,
    ""CorrelationId"" character varying(64) NULL,
    ""EventType"" character varying(200) NOT NULL,
    ""EntityType"" character varying(100) NOT NULL,
    ""EntityId"" character varying(200) NOT NULL,
    ""Payload"" jsonb NOT NULL,
    ""OccurredAt"" timestamp with time zone NOT NULL,
    ""IngestedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ""IdempotencyKey"" character varying(128) NOT NULL,
    ""PrevEventHash"" character varying(64) NULL,
    CONSTRAINT ""PK_events"" PRIMARY KEY (""EventId"", ""OccurredAt"")
) PARTITION BY RANGE (""OccurredAt"");");

            // ----------------------------------------------------------------
            // 4. Re-create the index set on the parent. Postgres 17 propagates
            //    parent indexes to every partition automatically as long as
            //    the partition column is in the index — all five hot-path
            //    indexes here include OccurredAt, so partition pruning + the
            //    partition-local indexes both work.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE UNIQUE INDEX ux_audit_events_tenant_idempotency
    ON audit.events (""TenantId"", ""IdempotencyKey"", ""OccurredAt"");");

            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_entity_time
    ON audit.events (""TenantId"", ""EntityType"", ""EntityId"", ""OccurredAt"");");

            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_type_time
    ON audit.events (""TenantId"", ""EventType"", ""OccurredAt"");");

            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_actor_time
    ON audit.events (""TenantId"", ""ActorUserId"", ""OccurredAt"");");

            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_correlation
    ON audit.events (""CorrelationId"");");

            // Partial index on the system-event case (TenantId IS NULL).
            // The HasFilter on the EF model is preserved verbatim.
            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_system_type_time
    ON audit.events (""EventType"", ""OccurredAt"")
    WHERE ""TenantId"" IS NULL;");

            // ----------------------------------------------------------------
            // 5. Pre-create monthly partitions covering 12 months back through
            //    6 months ahead. This window holds existing dev / pilot rows
            //    AND covers the next half-year of writes without operator
            //    intervention. The recurring scheduled-task in
            //    tools/migrations/audit-events-create-partition.sql appends
            //    a new month a few days before the previous frontier closes.
            // ----------------------------------------------------------------
            for (var i = 0; i < PreCreatedPartitionCount; i++)
            {
                var start = FirstPartitionMonth.AddMonths(i);
                var end = start.AddMonths(1);
                var partitionName = $"events_{start:yyyy_MM}";

                migrationBuilder.Sql($@"
CREATE TABLE audit.{partitionName}
    PARTITION OF audit.events
    FOR VALUES FROM ('{start:yyyy-MM-dd} 00:00:00+00') TO ('{end:yyyy-MM-dd} 00:00:00+00');");
            }

            // ----------------------------------------------------------------
            // 6. Re-apply RLS. Partitioned parents propagate RLS to every
            //    partition (Postgres 17). The policy text is identical to
            //    Add_RLS_Policies / Make_TenantId_Nullable's FU-2 wording —
            //    fail-closed COALESCE with '0' default + NULL-tenant
            //    system-event admit clause.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(@"
ALTER TABLE audit.events ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
ALTER TABLE audit.events FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation_events ON audit.events
    USING (
        ""TenantId"" IS NULL
        OR ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
    )
    WITH CHECK (
        ""TenantId"" IS NULL
        OR ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
    );");

            // ----------------------------------------------------------------
            // 7. Re-grant the role privileges. ALTER DEFAULT PRIVILEGES from
            //    the F5 grants migration covers future tables, but we re-grant
            //    explicitly on the rebuilt parent for defence-in-depth (and
            //    because the partitions inherit the parent's grants — they
            //    don't pick up the schema-level default at CREATE time).
            // ----------------------------------------------------------------
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT ON audit.events TO nscim_app;");

            // Per-partition grants — the ALTER DEFAULT PRIVILEGES catches
            // future partitions automatically (each new CREATE TABLE inside
            // the schema picks up the default), but the back-coverage ones
            // we just created in step 5 missed the default-privileges window
            // for the same transaction (defaults apply to *new* objects).
            // Explicit grants close the gap.
            for (var i = 0; i < PreCreatedPartitionCount; i++)
            {
                var start = FirstPartitionMonth.AddMonths(i);
                var partitionName = $"events_{start:yyyy_MM}";
                migrationBuilder.Sql(
                    $@"GRANT SELECT, INSERT ON audit.{partitionName} TO nscim_app;");
            }

            // ----------------------------------------------------------------
            // 8. Copy rows from the legacy table into the partitioned table.
            //    INSERT-SELECT routes each row into its month-partition via
            //    the partition key. This is the "heavy" step — wall-clock
            //    cost scales with the source row count.
            //
            //    Note: rows whose OccurredAt falls outside the pre-created
            //    range will fail the INSERT. The 12-back / 6-ahead window
            //    is generous; if a deployment ever encounters older rows,
            //    pre-pend extra partitions before re-running this migration
            //    in a fix-forward branch.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(@"
INSERT INTO audit.events (
    ""EventId"",
    ""TenantId"",
    ""ActorUserId"",
    ""CorrelationId"",
    ""EventType"",
    ""EntityType"",
    ""EntityId"",
    ""Payload"",
    ""OccurredAt"",
    ""IngestedAt"",
    ""IdempotencyKey"",
    ""PrevEventHash""
)
SELECT
    ""EventId"",
    ""TenantId"",
    ""ActorUserId"",
    ""CorrelationId"",
    ""EventType"",
    ""EntityType"",
    ""EntityId"",
    ""Payload"",
    ""OccurredAt"",
    ""IngestedAt"",
    ""IdempotencyKey"",
    ""PrevEventHash""
FROM audit.events_legacy_v1;");

            // ----------------------------------------------------------------
            // 9. Drop the legacy table. Rows have been copied; keeping it
            //    around invites confusion (queries against the wrong name)
            //    and wastes disk. An operator with a pre-deploy backup
            //    (step 2 of the migration runbook) can restore from that
            //    if the rebuild is ever proven unsound.
            //
            //    Operators who want a longer "training-wheels" window can
            //    comment this DROP out and revisit in a follow-up
            //    migration; the deferred-action note in CHANGELOG.md
            //    captures that option.
            // ----------------------------------------------------------------
            migrationBuilder.Sql(@"
DROP TABLE audit.events_legacy_v1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse step 9: re-create the legacy heap (empty).
            migrationBuilder.Sql(@"
CREATE TABLE audit.events_legacy_v1 (
    ""EventId"" uuid NOT NULL DEFAULT gen_random_uuid(),
    ""TenantId"" bigint NULL,
    ""ActorUserId"" uuid NULL,
    ""CorrelationId"" character varying(64) NULL,
    ""EventType"" character varying(200) NOT NULL,
    ""EntityType"" character varying(100) NOT NULL,
    ""EntityId"" character varying(200) NOT NULL,
    ""Payload"" jsonb NOT NULL,
    ""OccurredAt"" timestamp with time zone NOT NULL,
    ""IngestedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ""IdempotencyKey"" character varying(128) NOT NULL,
    ""PrevEventHash"" character varying(64) NULL,
    CONSTRAINT ""PK_events_legacy_v1"" PRIMARY KEY (""EventId"")
);");

            // Reverse step 8: copy partitioned rows back.
            migrationBuilder.Sql(@"
INSERT INTO audit.events_legacy_v1 (
    ""EventId"",
    ""TenantId"",
    ""ActorUserId"",
    ""CorrelationId"",
    ""EventType"",
    ""EntityType"",
    ""EntityId"",
    ""Payload"",
    ""OccurredAt"",
    ""IngestedAt"",
    ""IdempotencyKey"",
    ""PrevEventHash""
)
SELECT
    ""EventId"",
    ""TenantId"",
    ""ActorUserId"",
    ""CorrelationId"",
    ""EventType"",
    ""EntityType"",
    ""EntityId"",
    ""Payload"",
    ""OccurredAt"",
    ""IngestedAt"",
    ""IdempotencyKey"",
    ""PrevEventHash""
FROM audit.events;");

            // Reverse step 7 + 6: drop policy + grants on the partitioned table.
            migrationBuilder.Sql(@"DROP POLICY IF EXISTS tenant_isolation_events ON audit.events;");
            migrationBuilder.Sql(@"ALTER TABLE audit.events NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE audit.events DISABLE ROW LEVEL SECURITY;");

            // Reverse step 5: drop partitions. CASCADE because the table-level
            // DROP can't cope with the partition relationship otherwise.
            migrationBuilder.Sql(@"DROP TABLE audit.events CASCADE;");

            // Rename the legacy heap back to events. The Down path here
            // assumes a fresh-from-Up shape where the legacy heap was just
            // re-created in the first Down step. (A real production rollback
            // would prefer PITR over the Down path; see migration xml-comment.)
            migrationBuilder.Sql(@"ALTER TABLE audit.events_legacy_v1 RENAME TO events;");
            migrationBuilder.Sql(@"ALTER TABLE audit.events RENAME CONSTRAINT ""PK_events_legacy_v1"" TO ""PK_events"";");

            // Re-create the original indexes (single-column key shape).
            migrationBuilder.Sql(@"
CREATE UNIQUE INDEX ux_audit_events_tenant_idempotency
    ON audit.events (""TenantId"", ""IdempotencyKey"");");
            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_entity_time
    ON audit.events (""TenantId"", ""EntityType"", ""EntityId"", ""OccurredAt"");");
            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_type_time
    ON audit.events (""TenantId"", ""EventType"", ""OccurredAt"");");
            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_actor_time
    ON audit.events (""TenantId"", ""ActorUserId"", ""OccurredAt"");");
            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_correlation
    ON audit.events (""CorrelationId"");");
            migrationBuilder.Sql(@"
CREATE INDEX ix_audit_events_system_type_time
    ON audit.events (""EventType"", ""OccurredAt"")
    WHERE ""TenantId"" IS NULL;");

            // Re-enable RLS on the un-partitioned heap.
            migrationBuilder.Sql(@"ALTER TABLE audit.events ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE audit.events FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation_events ON audit.events
    USING (
        ""TenantId"" IS NULL
        OR ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
    )
    WITH CHECK (
        ""TenantId"" IS NULL
        OR ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
    );");
            migrationBuilder.Sql(@"GRANT SELECT, INSERT ON audit.events TO nscim_app;");

            // Re-instate the FK from notifications.
            migrationBuilder.Sql(@"
ALTER TABLE audit.notifications
    ADD CONSTRAINT ""FK_notifications_events_EventId""
    FOREIGN KEY (""EventId"") REFERENCES audit.events (""EventId"") ON DELETE CASCADE;");
        }
    }
}
