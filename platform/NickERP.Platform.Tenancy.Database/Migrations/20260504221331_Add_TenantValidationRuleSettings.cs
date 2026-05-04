using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <summary>
    /// Sprint 28 — per-tenant validation-rule on/off flags.
    ///
    /// <para>
    /// New table <c>tenancy.tenant_validation_rule_settings</c> records the
    /// admin's "disable rule X for tenant Y" decision. Rows are sparse:
    /// missing means "enabled by default", so the admin only persists the
    /// rules they want silenced.
    /// </para>
    ///
    /// <para>
    /// Tenant-scoped + RLS-enforced. Mirrors the FORCE-RLS / fail-closed
    /// pattern used by the 180+ <c>tenant_isolation_*</c> policies on the
    /// other platform schemas (the <c>'0'</c> default in the COALESCE
    /// blocks rows when no <c>app.tenant_id</c> is set, so a misconfigured
    /// caller cannot bypass RLS).
    /// </para>
    /// </summary>
    public partial class Add_TenantValidationRuleSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_validation_rule_settings",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    RuleId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_validation_rule_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_validation_rule_settings_tenant_rule",
                schema: "tenancy",
                table: "tenant_validation_rule_settings",
                columns: new[] { "TenantId", "RuleId" },
                unique: true);

            // Defense-in-depth tenancy. Mirrors the pattern in the other
            // 180+ tenant_isolation_* policies — FORCE makes the policy
            // apply even to the table owner; the COALESCE('0') default
            // fail-closes when app.tenant_id is unset.
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_validation_rule_settings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_validation_rule_settings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation_tenant_validation_rule_settings
  ON tenancy.tenant_validation_rule_settings
  USING (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
  WITH CHECK (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
");

            // Grants: app code needs SELECT (engine + admin list) + INSERT
            // (admin disables for the first time) + UPDATE (admin flips an
            // existing row). DELETE intentionally omitted — re-enabling
            // happens via UPDATE Enabled=true so the audit trail of
            // who-flipped-what survives.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT, UPDATE ON tenancy.tenant_validation_rule_settings TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_tenant_validation_rule_settings ON tenancy.tenant_validation_rule_settings;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_validation_rule_settings NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_validation_rule_settings DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "tenant_validation_rule_settings",
                schema: "tenancy");
        }
    }
}
