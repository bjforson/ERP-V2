using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint S+3 / B-queues — adds the five remaining per-stage work
    /// queues that flow downstream of <c>queue_split_detection</c>:
    /// image-analysis → decision-agent → audit-assignment → audit-review →
    /// submission. Tables are NOT EF entities — like
    /// <c>queue_split_detection</c> in <c>Add_QueueingPlatform</c> they're
    /// accessed via raw Npgsql by <c>PostgresQueue&lt;TPayload&gt;</c> for
    /// the FOR UPDATE SKIP LOCKED claim path. Schema mirrors the
    /// reference table 1:1; only the table name differs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Design parity.</b> Each of the five tables uses the exact
    /// shape of <c>queue_split_detection</c> — same column names, same
    /// types, same defaults, same indexes, same RLS posture. Drift
    /// between queue tables would force per-queue special-casing in
    /// <c>PostgresQueue&lt;TPayload&gt;</c>, which is structurally one
    /// implementation generic over <typeparamref>TPayload</typeparamref>
    /// — so the SQL stays identical and the loop below emits all five
    /// from the same template list.
    /// </para>
    /// <para>
    /// <b>RLS posture.</b> Per memory <c>reference_rls_now_enforces.md</c>:
    /// <c>ENABLE</c> + <c>FORCE ROW LEVEL SECURITY</c> with a
    /// <c>tenant_isolation_*</c> policy that fails closed (COALESCE'd
    /// to <c>'0'</c>) when no <c>app.tenant_id</c> session var is set.
    /// Identical to the existing five tables on <c>nickerp_inspection</c>
    /// (work_items, work_item_transitions, queue_split_detection, plus
    /// the 30+ EF entities covered by <c>Add_RLS_Policies</c>).
    /// </para>
    /// <para>
    /// <b>Stub consumers won't actually read these tables in Sprint
    /// S+3.</b> The five consumer scaffolds in
    /// <c>NickERP.Inspection.Application.Workflows</c> log a TODO and
    /// complete; no producer is wired yet. The tables exist so the
    /// queueing platform's metrics + janitor sweep can include them
    /// from S+3 onwards (registered via
    /// <c>AddPostgresQueue&lt;TPayload&gt;</c> in
    /// <c>NickERP.Inspection.Web</c>).
    /// </para>
    /// </remarks>
    public partial class Add_S3_QueueTables : Migration
    {
        /// <summary>
        /// The five new queue table suffixes — appended to
        /// <c>queue_</c> for the physical table name. Mirrors the
        /// <c>opts.Name</c> values registered in
        /// <c>NickERP.Inspection.Web</c>'s
        /// <c>AddPostgresQueue&lt;TPayload&gt;</c> calls.
        /// </summary>
        private static readonly string[] QueueSuffixes =
        {
            "image_analysis",
            "decision_agent",
            "audit_assignment",
            "audit_review",
            "submission"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            foreach (var suffix in QueueSuffixes)
            {
                var table = $"queue_{suffix}";

                // ---------------------------------------------------------
                // Table — identical shape to inspection.queue_split_detection
                // (see Add_QueueingPlatform). Quoted PascalCase column names
                // match the platform's PostgresQueue<TPayload> raw-SQL
                // expectations.
                // ---------------------------------------------------------
                migrationBuilder.Sql($@"
                    CREATE TABLE inspection.{table} (
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
                    );");

                // Idempotency-key uniqueness — duplicate enqueues collapse
                // into a single row.
                migrationBuilder.Sql(
                    $"CREATE UNIQUE INDEX ux_{table}_idempotency_key "
                    + $"ON inspection.{table} (\"IdempotencyKey\");");

                // Partial index — the hot path for unclaimed-and-ready
                // rows. Used by ClaimAsync to skip past in-flight rows
                // cheaply.
                migrationBuilder.Sql(
                    $"CREATE INDEX ix_{table}_available_unclaimed "
                    + $"ON inspection.{table} (\"AvailableAt\") "
                    + "WHERE \"ClaimedBy\" IS NULL;");

                // Lookup by work-item — used when the producer or admin
                // tooling wants to find every queue row dispatched for
                // a work item.
                migrationBuilder.Sql(
                    $"CREATE INDEX ix_{table}_work_item_id "
                    + $"ON inspection.{table} (\"WorkItemId\");");

                // Lookup by lease expiry — the janitor reclaims rows
                // whose ClaimedUntil has passed; partial keeps the
                // index small (NULL claimed_until = unclaimed = not in
                // the janitor's scope).
                migrationBuilder.Sql(
                    $"CREATE INDEX ix_{table}_claimed_until "
                    + $"ON inspection.{table} (\"ClaimedUntil\") "
                    + "WHERE \"ClaimedBy\" IS NOT NULL;");

                // ---------------------------------------------------------
                // RLS — fail-closed tenant isolation. Same policy shape
                // as the inspection schema's other tenant-owned tables
                // (see Add_RLS_Policies + Add_QueueingPlatform).
                // ---------------------------------------------------------
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

            // Reverse order: RLS off, then drop indexes + table. Each
            // queue is independent — order within the loop doesn't
            // matter, but for symmetry we walk the same list.
            foreach (var suffix in QueueSuffixes)
            {
                var table = $"queue_{suffix}";

                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation_{table} ON inspection.{table};");
                migrationBuilder.Sql($"ALTER TABLE inspection.{table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE inspection.{table} DISABLE ROW LEVEL SECURITY;");

                migrationBuilder.Sql($"DROP INDEX IF EXISTS inspection.ix_{table}_claimed_until;");
                migrationBuilder.Sql($"DROP INDEX IF EXISTS inspection.ix_{table}_work_item_id;");
                migrationBuilder.Sql($"DROP INDEX IF EXISTS inspection.ix_{table}_available_unclaimed;");
                migrationBuilder.Sql($"DROP INDEX IF EXISTS inspection.ux_{table}_idempotency_key;");
                migrationBuilder.Sql($"DROP TABLE IF EXISTS inspection.{table};");
            }
        }
    }
}
