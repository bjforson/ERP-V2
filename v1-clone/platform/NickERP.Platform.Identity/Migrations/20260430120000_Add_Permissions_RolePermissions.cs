using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Identity.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Phase 2 of the role-catalog overhaul (2026-04-30). Adds the
    /// <c>identity.permissions</c> catalogue (52 rows seeded by the
    /// bootstrap CLI from <c>NickFinance.WebApp.Identity.Permissions</c>)
    /// and the <c>identity.role_permissions</c> join (composite PK,
    /// seeded from <c>GradePermissions.ForGrade(roleName)</c>). The
    /// <c>NickFinance</c> Razor pages now gate via
    /// <c>[Authorize(Policy = Permissions.X)]</c>; the
    /// <c>DynamicAuthorizationPolicyProvider</c> resolves each policy
    /// name to a one-shot <c>PermissionRequirement</c>, and the
    /// <c>PermissionAuthorizationHandler</c> checks the user's bundle
    /// via <c>IPermissionService</c>.
    /// </remarks>
    public partial class Add_Permissions_RolePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "identity",
                columns: table => new
                {
                    permission_id = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permissions", x => x.permission_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_permissions_name",
                schema: "identity",
                table: "permissions",
                column: "name",
                unique: true);

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "identity",
                columns: table => new
                {
                    role_id = table.Column<short>(type: "smallint", nullable: false),
                    permission_id = table.Column<short>(type: "smallint", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => new { x.role_id, x.permission_id });
                    table.ForeignKey(
                        name: "fk_role_permissions_roles",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "role_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_permissions_permissions",
                        column: x => x.permission_id,
                        principalSchema: "identity",
                        principalTable: "permissions",
                        principalColumn: "permission_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_role_permissions_permission",
                schema: "identity",
                table: "role_permissions",
                column: "permission_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "identity");
        }
    }
}
