using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <summary>
    /// Sprint 35 / B8.2 — per-tenant feature flag table + generic
    /// tenant key/value settings table.
    ///
    /// <para>
    /// Two new tables in the <c>tenancy</c> schema:
    /// <list type="bullet">
    ///   <item><c>feature_flags</c> — admin's "flag X is on/off for
    ///   tenant Y" decision. Sparse rows; missing means "use the
    ///   default the calling code passes to
    ///   <c>IFeatureFlagService.IsEnabledAsync</c>".</item>
    ///   <item><c>tenant_settings</c> — generic key/value setting
    ///   table. Sparse rows; missing means "use the default the calling
    ///   code passes to <c>ITenantSettingsService.GetAsync</c>".</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Tenant-scoped + RLS-enforced. Mirrors the
    /// <c>Add_TenantCompletenessAndSlaSettings</c> migration's posture:
    /// FORCE RLS + COALESCE-to-'0' fail-closed default + nscim_app
    /// SELECT/INSERT/UPDATE grants; DELETE intentionally omitted so the
    /// audit trail of past flips on the row itself survives.
    /// </para>
    /// </summary>
    public partial class Add_FeatureFlags_And_TenantSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feature_flags",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    FlagKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_flags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_settings",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    SettingKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_feature_flags_tenant_flag",
                schema: "tenancy",
                table: "feature_flags",
                columns: new[] { "TenantId", "FlagKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenant_settings_tenant_key",
                schema: "tenancy",
                table: "tenant_settings",
                columns: new[] { "TenantId", "SettingKey" },
                unique: true);

            // Defense-in-depth tenancy. Mirrors the pattern in the other
            // 180+ tenant_isolation_* policies — FORCE makes the policy
            // apply even to the table owner; the COALESCE('0') default
            // fail-closes when app.tenant_id is unset.
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.feature_flags ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.feature_flags FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation_feature_flags
  ON tenancy.feature_flags
  USING (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
  WITH CHECK (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
");

            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_settings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_settings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation_tenant_settings
  ON tenancy.tenant_settings
  USING (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
  WITH CHECK (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
");

            // Grants: app code needs SELECT (read path) + INSERT (admin
            // toggles for the first time) + UPDATE (admin flips an
            // existing row). DELETE intentionally omitted — re-toggling
            // happens via UPDATE so the audit trail of who-flipped-what
            // survives on the row itself.
            migrationBuilder.Sql(@"
GRANT SELECT, INSERT, UPDATE ON tenancy.feature_flags TO nscim_app;
GRANT SELECT, INSERT, UPDATE ON tenancy.tenant_settings TO nscim_app;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_feature_flags ON tenancy.feature_flags;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.feature_flags NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.feature_flags DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_tenant_settings ON tenancy.tenant_settings;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_settings NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_settings DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "feature_flags",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "tenant_settings",
                schema: "tenancy");
        }
    }
}
