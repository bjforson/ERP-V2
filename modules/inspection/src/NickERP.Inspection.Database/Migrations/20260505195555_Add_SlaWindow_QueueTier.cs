using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <inheritdoc />
    public partial class Add_SlaWindow_QueueTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QueueTier",
                schema: "inspection",
                table: "sla_window",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "QueueTierIsManual",
                schema: "inspection",
                table: "sla_window",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_sla_window_tenant_tier_state",
                schema: "inspection",
                table: "sla_window",
                columns: new[] { "TenantId", "QueueTier", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sla_window_tenant_tier_state",
                schema: "inspection",
                table: "sla_window");

            migrationBuilder.DropColumn(
                name: "QueueTier",
                schema: "inspection",
                table: "sla_window");

            migrationBuilder.DropColumn(
                name: "QueueTierIsManual",
                schema: "inspection",
                table: "sla_window");
        }
    }
}
