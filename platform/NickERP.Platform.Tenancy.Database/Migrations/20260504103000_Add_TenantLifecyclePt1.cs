using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <summary>
    /// Sprint 18 — Tenant lifecycle Pt 1. Replaces the prior
    /// <c>IsActive bool</c> with a <c>TenantState</c> enum (Active /
    /// Suspended / SoftDeleted / PendingHardPurge), adds soft-delete +
    /// retention columns, and creates the cross-tenant
    /// <c>tenant_purge_log</c> table that survives every hard-purge.
    /// </summary>
    /// <remarks>
    /// Migration order:
    /// <list type="number">
    ///   <item><description>Add <c>State</c> column with DEFAULT 0 (Active).</description></item>
    ///   <item><description>Backfill <c>State</c> from <c>IsActive</c>: TRUE => 0 (Active), FALSE => 10 (Suspended).</description></item>
    ///   <item><description>Add lifecycle columns (DeletedAt, DeletedByUserId, DeletionReason, RetentionDays, HardPurgeAfter).</description></item>
    ///   <item><description>Drop <c>IsActive</c> bool — superseded by State.</description></item>
    ///   <item><description>Create <c>ix_tenants_state</c> index.</description></item>
    ///   <item><description>Create <c>tenancy.tenant_purge_log</c> table (cross-tenant audit-survival log).</description></item>
    /// </list>
    /// Down() reverses each step in inverse order; data lost in the drop
    /// of the new columns is unrecoverable but matches the EF expectation.
    /// </remarks>
    public partial class Add_TenantLifecyclePt1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (1) Add State column with default Active(0). NOT NULL with a
            // SQL default so the existing single seed row is set
            // immediately; backfill in step (2) is a belt-and-suspenders
            // re-affirmation of that default keyed off IsActive.
            migrationBuilder.AddColumn<int>(
                name: "State",
                schema: "tenancy",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // (2) Backfill from IsActive — old "active" rows stay Active,
            // old "inactive" rows become Suspended (the closest semantic
            // match in the new enum). SoftDeleted has no historical
            // analogue; ops will mark explicit soft-deletes going forward.
            migrationBuilder.Sql(@"
UPDATE tenancy.tenants
SET ""State"" = CASE WHEN ""IsActive"" = TRUE THEN 0 ELSE 10 END;
");

            // (3) Lifecycle columns. All nullable except RetentionDays
            // (which gets a sensible default of 90 days per locked
            // architectural decision in the sprint brief).
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "tenancy",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedByUserId",
                schema: "tenancy",
                table: "tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                schema: "tenancy",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                schema: "tenancy",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HardPurgeAfter",
                schema: "tenancy",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            // (4) Drop the legacy IsActive bool — superseded by State.
            // Computed property on the Tenant entity preserves the
            // truthiness API for callers that haven't migrated.
            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "tenancy",
                table: "tenants");

            // (5) Index on State — supports the "active tenants" scope
            // and the "all tenants pending hard-purge" admin view that
            // both walk the table by state today.
            migrationBuilder.CreateIndex(
                name: "ix_tenants_state",
                schema: "tenancy",
                table: "tenants",
                column: "State");

            // (6) Cross-tenant audit-survival log. Lives in tenancy
            // schema; deliberately NOT under RLS — the rows describe
            // tenants that no longer exist, so any tenant-scoped filter
            // would hide them.
            migrationBuilder.CreateTable(
                name: "tenant_purge_log",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    TenantCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PurgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PurgedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeletionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SoftDeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowCounts = table.Column<string>(type: "jsonb", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_purge_log", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_purge_log_purgedat",
                schema: "tenancy",
                table: "tenant_purge_log",
                column: "PurgedAt");

            // GRANT to nscim_app so app code can both INSERT during a
            // hard-purge and SELECT to render the admin dashboard.
            // Schema-level grants from prior migrations cover SELECT on
            // newly-created tables but not INSERT — we set both
            // explicitly here for clarity.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT ON tenancy.tenant_purge_log TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rebuild IsActive bool first so we can backfill from State
            // before dropping State.
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "tenancy",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(@"
UPDATE tenancy.tenants
SET ""IsActive"" = CASE WHEN ""State"" = 0 THEN TRUE ELSE FALSE END;
");

            migrationBuilder.DropTable(
                name: "tenant_purge_log",
                schema: "tenancy");

            migrationBuilder.DropIndex(
                name: "ix_tenants_state",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "State",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "RetentionDays",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "HardPurgeAfter",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
