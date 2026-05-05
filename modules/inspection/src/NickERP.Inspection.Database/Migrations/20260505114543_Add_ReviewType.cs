using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <inheritdoc />
    public partial class Add_ReviewType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReviewQueue",
                schema: "inspection",
                table: "cases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                schema: "inspection",
                table: "analyst_reviews",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                schema: "inspection",
                table: "analyst_reviews",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewType",
                schema: "inspection",
                table: "analyst_reviews",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "StartedByUserId",
                schema: "inspection",
                table: "analyst_reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_queue_state_time",
                schema: "inspection",
                table: "cases",
                columns: new[] { "TenantId", "ReviewQueue", "State", "OpenedAt" },
                descending: new[] { false, true, false, false });

            migrationBuilder.CreateIndex(
                name: "ix_analyst_reviews_tenant_type_time",
                schema: "inspection",
                table: "analyst_reviews",
                columns: new[] { "TenantId", "ReviewType", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cases_tenant_queue_state_time",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropIndex(
                name: "ix_analyst_reviews_tenant_type_time",
                schema: "inspection",
                table: "analyst_reviews");

            migrationBuilder.DropColumn(
                name: "ReviewQueue",
                schema: "inspection",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                schema: "inspection",
                table: "analyst_reviews");

            migrationBuilder.DropColumn(
                name: "Outcome",
                schema: "inspection",
                table: "analyst_reviews");

            migrationBuilder.DropColumn(
                name: "ReviewType",
                schema: "inspection",
                table: "analyst_reviews");

            migrationBuilder.DropColumn(
                name: "StartedByUserId",
                schema: "inspection",
                table: "analyst_reviews");
        }
    }
}
