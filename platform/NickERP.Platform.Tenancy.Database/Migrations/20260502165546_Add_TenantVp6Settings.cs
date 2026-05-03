using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <inheritdoc />
    public partial class Add_TenantVp6Settings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowMultiServiceMembership",
                schema: "tenancy",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "CaseVisibilityModel",
                schema: "tenancy",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                schema: "tenancy",
                table: "tenants",
                keyColumn: "Id",
                keyValue: 1L,
                column: "AllowMultiServiceMembership",
                value: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowMultiServiceMembership",
                schema: "tenancy",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "CaseVisibilityModel",
                schema: "tenancy",
                table: "tenants");
        }
    }
}
