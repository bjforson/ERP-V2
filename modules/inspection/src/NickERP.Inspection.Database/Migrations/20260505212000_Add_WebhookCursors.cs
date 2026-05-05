using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 41 / Phase C — adds <c>inspection.webhook_cursors</c>.
    ///
    /// <para>
    /// Per-tenant per-adapter cursor for the outbound webhook
    /// dispatcher. One row per (TenantId, AdapterName) — unique partial
    /// index <c>ux_webhook_cursors_tenant_adapter</c> enforces the
    /// upsert-on-cursor-advance idiom in <c>WebhookDispatchWorker</c>.
    /// </para>
    ///
    /// <para>
    /// Tenant-scoped + RLS-enforced. <c>nscim_app</c> gets SELECT +
    /// INSERT + UPDATE — UPDATE is required because the dispatcher
    /// advances the cursor on every successful tick that processes new
    /// events. No DELETE — cursors persist for the life of an adapter
    /// configuration.
    /// </para>
    /// </summary>
    public partial class Add_WebhookCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_cursors",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AdapterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastProcessedEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_cursors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_webhook_cursors_tenant_adapter",
                schema: "inspection",
                table: "webhook_cursors",
                columns: new[] { "TenantId", "AdapterName" },
                unique: true);

            // RLS — same posture as every other inspection table.
            migrationBuilder.Sql("ALTER TABLE inspection.webhook_cursors ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.webhook_cursors FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_webhook_cursors ON inspection.webhook_cursors "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");

            // SELECT/INSERT/UPDATE — UPDATE required for cursor
            // advance. No DELETE; cursors persist for the life of an
            // adapter configuration.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT, UPDATE ON inspection.webhook_cursors TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_webhook_cursors ON inspection.webhook_cursors;");
            migrationBuilder.Sql("ALTER TABLE inspection.webhook_cursors NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.webhook_cursors DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "webhook_cursors",
                schema: "inspection");
        }
    }
}
