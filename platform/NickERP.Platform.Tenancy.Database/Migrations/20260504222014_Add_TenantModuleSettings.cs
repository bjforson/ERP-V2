using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <inheritdoc />
    public partial class Add_TenantModuleSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_module_settings",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    ModuleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_module_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_module_settings_tenant_module",
                schema: "tenancy",
                table: "tenant_module_settings",
                columns: new[] { "TenantId", "ModuleId" },
                unique: true);

            // Sprint 29 — tenant-isolation RLS. tenant_module_settings is
            // tenant-scoped (ITenantOwned) so it follows the same
            // FORCE ROW LEVEL SECURITY + COALESCE-fail-closed pattern as
            // every other tenant-owned table across the suite. The
            // policy reads app.tenant_id pushed by
            // TenantConnectionInterceptor and rejects writes whose
            // TenantId doesn't match the session var.
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_module_settings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_module_settings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_tenant_module_settings "
                + "ON tenancy.tenant_module_settings "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_tenant_module_settings "
                + "ON tenancy.tenant_module_settings;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_module_settings NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE tenancy.tenant_module_settings DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "tenant_module_settings",
                schema: "tenancy");
        }
    }
}
