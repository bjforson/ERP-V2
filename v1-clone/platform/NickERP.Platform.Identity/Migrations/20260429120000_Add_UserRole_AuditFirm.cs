using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Identity.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Phase 1 of the role-catalog overhaul (2026-04-29). Adds the
    /// <c>audit_firm</c> column to <c>identity.user_roles</c> so
    /// <see cref="RoleNames.ExternalAuditor"/> grants can carry the
    /// firm name for the audit trail. Nullable; ignored for every other
    /// role.
    /// </remarks>
    public partial class Add_UserRole_AuditFirm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audit_firm",
                schema: "identity",
                table: "user_roles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audit_firm",
                schema: "identity",
                table: "user_roles");
        }
    }
}
