using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 50 / FU-cursor-state-persistence — durable cursor state
    /// for cursor-sync scanner adapters. Adds
    /// <c>inspection.scanner_cursor_sync_states</c> with FORCE RLS +
    /// COALESCE-to-'0' fail-closed default, mirroring the Sprint 41
    /// <c>Add_Sprint41_GovernanceTables</c> shape.
    ///
    /// <para>
    /// Sprint 24's <c>AseSyncWorker</c> held cursor state in-memory; on
    /// host restart the cursor reset to empty + the unique
    /// <c>Scan.IdempotencyKey</c> index dedupedup the replays. That works
    /// for a single-host deploy but the multi-host pilot dictates
    /// durability so a fresh host doesn't re-pull a backlog the previous
    /// host already drained, and so two hosts converge on the same
    /// monotonic cursor instead of each advancing private state.
    /// </para>
    ///
    /// <para>
    /// One row per <c>(TenantId, ScannerDeviceTypeId, AdapterName)</c>;
    /// <c>nscim_app</c> gets SELECT/INSERT/UPDATE — no DELETE so
    /// removing a scanner-type row from configuration doesn't drop the
    /// cursor (re-adding the adapter resumes from where it left off).
    /// </para>
    /// </summary>
    public partial class Add_ScannerCursorSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scanner_cursor_sync_states",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScannerDeviceTypeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AdapterName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastCursorValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LastAdvancedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanner_cursor_sync_states", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cursor_sync_state_tenant",
                schema: "inspection",
                table: "scanner_cursor_sync_states",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_cursor_sync_state_tenant_type_adapter",
                schema: "inspection",
                table: "scanner_cursor_sync_states",
                columns: new[] { "TenantId", "ScannerDeviceTypeId", "AdapterName" },
                unique: true);

            // ----- RLS posture (mirrors Add_Sprint41_GovernanceTables) -----
            // ENABLE + FORCE Row Level Security
            // CREATE POLICY tenant_isolation_scanner_cursor_sync_states
            // COALESCE(current_setting('app.tenant_id', true), '0')::bigint
            //   so unset → fail-closed at TenantId=0.
            migrationBuilder.Sql("ALTER TABLE inspection.scanner_cursor_sync_states ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.scanner_cursor_sync_states FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_scanner_cursor_sync_states "
                + "ON inspection.scanner_cursor_sync_states "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");

            // ----- Grants ----------------------------------------------
            // SELECT/INSERT/UPDATE only — no DELETE so removing a
            // scanner-type from configuration doesn't drop the cursor;
            // re-adding the adapter resumes from where it left off.
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE ON inspection.scanner_cursor_sync_states TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_scanner_cursor_sync_states "
                + "ON inspection.scanner_cursor_sync_states;");
            migrationBuilder.Sql(
                "ALTER TABLE inspection.scanner_cursor_sync_states NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE inspection.scanner_cursor_sync_states DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "scanner_cursor_sync_states",
                schema: "inspection");
        }
    }
}
