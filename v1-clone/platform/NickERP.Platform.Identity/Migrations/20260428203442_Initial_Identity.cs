using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Identity.Migrations
{
    /// <inheritdoc />
    public partial class Initial_Identity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "identity",
                columns: table => new
                {
                    role_id = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.role_id);
                });

            migrationBuilder.CreateTable(
                name: "security_audit_log",
                schema: "identity",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<short>(type: "smallint", nullable: false),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    target_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    result = table.Column<short>(type: "smallint", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_audit_log", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    internal_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cf_access_sub = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.internal_user_id);
                });

            migrationBuilder.CreateTable(
                name: "user_phones",
                schema: "identity",
                columns: table => new
                {
                    user_phone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_e164 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    verified = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_phones", x => x.user_phone_id);
                    table.ForeignKey(
                        name: "FK_user_phones_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "internal_user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "identity",
                columns: table => new
                {
                    user_role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<short>(type: "smallint", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => x.user_role_id);
                    table.ForeignKey(
                        name: "FK_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "role_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "internal_user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_roles_name",
                schema: "identity",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_tenant_action",
                schema: "identity",
                table: "security_audit_log",
                columns: new[] { "tenant_id", "action" });

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_tenant_user_when",
                schema: "identity",
                table: "security_audit_log",
                columns: new[] { "tenant_id", "user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_tenant_when",
                schema: "identity",
                table: "security_audit_log",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_user_phones_user_id",
                schema: "identity",
                table: "user_phones",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_phones_e164",
                schema: "identity",
                table: "user_phones",
                column: "phone_e164",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_role_id",
                schema: "identity",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_user_role_site",
                schema: "identity",
                table: "user_roles",
                columns: new[] { "user_id", "role_id", "site_id" });

            migrationBuilder.CreateIndex(
                name: "ux_users_cf_access_sub",
                schema: "identity",
                table: "users",
                column: "cf_access_sub",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_tenant_email",
                schema: "identity",
                table: "users",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            // Seed the canonical roles. Idempotent — `ON CONFLICT DO NOTHING`
            // makes a re-run safe (the migrator itself only runs Up() once,
            // but if an operator restores from a backup that already has
            // the rows, this still succeeds).
            migrationBuilder.Sql(@"
                INSERT INTO identity.roles (role_id, name, description) VALUES
                    (1, 'Custodian',   'Holds the petty-cash float, disburses approved vouchers.'),
                    (2, 'Approver',    'Approves submitted vouchers up to a band-defined limit.'),
                    (3, 'SiteManager', 'Site-wide approver, can override the band ladder for their site.'),
                    (4, 'FinanceLead', 'Finance officer — issues invoices, posts journals, closes periods.'),
                    (5, 'Auditor',     'Read-only access to vouchers, invoices, and the security audit log.'),
                    (6, 'Admin',       'Full access — user management, role grants, all module operations.')
                ON CONFLICT (role_id) DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_audit_log",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_phones",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");
        }
    }
}
