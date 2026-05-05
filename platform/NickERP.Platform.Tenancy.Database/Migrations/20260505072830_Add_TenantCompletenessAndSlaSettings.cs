using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <summary>
    /// Sprint 31 / B5 — per-tenant completeness-requirement settings and
    /// per-tenant SLA-window budget settings.
    ///
    /// <para>
    /// Two new tables in the <c>tenancy</c> schema:
    /// <list type="bullet">
    ///   <item><c>tenant_completeness_settings</c> — admin's "disable
    ///   completeness requirement X for tenant Y, or override its
    ///   threshold" decision. Sparse rows; missing means "enabled at
    ///   default threshold".</item>
    ///   <item><c>tenant_sla_settings</c> — admin's "the SLA window
    ///   <c>case.open_to_validated</c> for tenant Y is N minutes"
    ///   override. Sparse rows; missing means "use the engine default
    ///   budget".</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Tenant-scoped + RLS-enforced. Mirrors the
    /// <c>Add_TenantValidationRuleSettings</c> migration's posture (FORCE
    /// RLS + COALESCE-to-'0' fail-closed default + nscim_app SELECT/
    /// INSERT/UPDATE grants; DELETE intentionally omitted so the audit
    /// trail of past flips survives).
    /// </para>
    /// </summary>
    public partial class Add_TenantCompletenessAndSlaSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_completeness_settings",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    RequirementId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    MinThreshold = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_completeness_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_sla_settings",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    WindowName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetMinutes = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_sla_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_completeness_settings_tenant_req",
                schema: "tenancy",
                table: "tenant_completeness_settings",
                columns: new[] { "TenantId", "RequirementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenant_sla_settings_tenant_window",
                schema: "tenancy",
                table: "tenant_sla_settings",
                columns: new[] { "TenantId", "WindowName" },
                unique: true);

            // Defense-in-depth tenancy. Mirrors the pattern in the other
            // 180+ tenant_isolation_* policies — FORCE makes the policy
            // apply even to the table owner; the COALESCE('0') default
            // fail-closes when app.tenant_id is unset.
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_completeness_settings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_completeness_settings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation_tenant_completeness_settings
  ON tenancy.tenant_completeness_settings
  USING (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
  WITH CHECK (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
");

            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_sla_settings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_sla_settings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation_tenant_sla_settings
  ON tenancy.tenant_sla_settings
  USING (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
  WITH CHECK (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
");

            // Grants: app code needs SELECT (engine + admin list) +
            // INSERT (admin disables / overrides for the first time) +
            // UPDATE (admin flips an existing row). DELETE intentionally
            // omitted — re-enabling happens via UPDATE Enabled=true so
            // the audit trail of who-flipped-what survives.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT, UPDATE ON tenancy.tenant_completeness_settings TO nscim_app;
GRANT SELECT, INSERT, UPDATE ON tenancy.tenant_sla_settings TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_tenant_completeness_settings ON tenancy.tenant_completeness_settings;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_completeness_settings NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_completeness_settings DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_tenant_sla_settings ON tenancy.tenant_sla_settings;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_sla_settings NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_sla_settings DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "tenant_completeness_settings",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "tenant_sla_settings",
                schema: "tenancy");
        }
    }
}
