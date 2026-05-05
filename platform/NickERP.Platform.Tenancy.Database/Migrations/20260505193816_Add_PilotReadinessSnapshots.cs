using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <summary>
    /// Sprint 43 — adds the cross-tenant <c>tenancy.pilot_readiness_snapshots</c>
    /// table that <c>PilotReadinessService.GetReadinessAsync</c> writes one
    /// row per gate per refresh into. Append-only — no UPDATE / DELETE in
    /// the role grants so transitions Pass→Fail→Pass are auditable.
    /// </summary>
    /// <remarks>
    /// Same posture as <c>tenant_purge_log</c> + <c>tenant_export_requests</c>:
    /// not under RLS, cross-tenant by design (admin tooling). nscim_app
    /// gets SELECT + INSERT only; the dashboard writes a fresh snapshot
    /// per refresh and reads the latest per <c>(TenantId, GateId)</c>
    /// using the descending <c>ix_pilot_readiness_snapshots_tenant_gate_observedat</c>
    /// index.
    /// </remarks>
    public partial class Add_PilotReadinessSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pilot_readiness_snapshots",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    GateId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProofEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pilot_readiness_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pilot_readiness_snapshots_tenant_gate_observedat",
                schema: "tenancy",
                table: "pilot_readiness_snapshots",
                columns: new[] { "TenantId", "GateId", "ObservedAt" },
                descending: new[] { false, false, true });

            // Grants: app code needs SELECT (dashboard read) + INSERT
            // (each refresh writes one row per gate). UPDATE intentionally
            // omitted — snapshots are append-only by design so transitions
            // are auditable. DELETE intentionally omitted for the same
            // reason.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT ON tenancy.pilot_readiness_snapshots TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pilot_readiness_snapshots",
                schema: "tenancy");
        }
    }
}
