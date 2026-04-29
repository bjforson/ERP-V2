using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 9 / FU-icums-signing — per-tenant HMAC-SHA256 signing keys
    /// for the IcumsGh adapter's pre-emptive envelope-signing flow.
    ///
    /// <para>
    /// Mirrors the established phase-F1 RLS posture from
    /// <c>20260427211653_Add_RLS_Policies</c>: <c>ENABLE</c> +
    /// <c>FORCE ROW LEVEL SECURITY</c>, plus a
    /// <c>tenant_isolation_icums_signing_keys</c> policy comparing
    /// <c>"TenantId"</c> against <c>app.tenant_id</c> with a
    /// COALESCE-to-<c>'0'</c> fail-closed default for unauthenticated
    /// connections.
    /// </para>
    ///
    /// <para>
    /// Grants follow the spec: <c>SELECT, INSERT, UPDATE</c> — no
    /// <c>DELETE</c>. Keys are never deleted, only retired with
    /// <c>RetiredAt</c> + <c>VerificationOnlyUntil</c> set.
    /// </para>
    /// </summary>
    public partial class Add_IcumsSigningKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "icums_signing_keys",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    KeyMaterialEncrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VerificationOnlyUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_icums_signing_keys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_icums_signing_keys_tenant_active",
                schema: "inspection",
                table: "icums_signing_keys",
                columns: new[] { "TenantId", "ActivatedAt", "RetiredAt" });

            migrationBuilder.CreateIndex(
                name: "ux_icums_signing_keys_tenant_keyid",
                schema: "inspection",
                table: "icums_signing_keys",
                columns: new[] { "TenantId", "KeyId" },
                unique: true);

            // RLS: mirror phase-F1 tenant_isolation_<table> pattern. The
            // ALTER DEFAULT PRIVILEGES from 20260427221059_Add_NscimAppRole_Grants
            // already grants nscim_app the standard CRUD set on every new
            // table in this schema, including DELETE. The spec for
            // FU-icums-signing forbids DELETE on this table — so we
            // explicitly REVOKE it after the inherited GRANT. Keys are
            // retired (RetiredAt set), never deleted.
            migrationBuilder.Sql(
                "ALTER TABLE inspection.icums_signing_keys ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE inspection.icums_signing_keys FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_icums_signing_keys ON inspection.icums_signing_keys "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");

            // Spec-mandated: SELECT, INSERT, UPDATE only — no DELETE.
            // ALTER DEFAULT PRIVILEGES grants CRUD; revoke DELETE here.
            migrationBuilder.Sql(
                "REVOKE DELETE ON inspection.icums_signing_keys FROM nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_icums_signing_keys ON inspection.icums_signing_keys;");
            migrationBuilder.Sql(
                "ALTER TABLE inspection.icums_signing_keys NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE inspection.icums_signing_keys DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "icums_signing_keys",
                schema: "inspection");
        }
    }
}
