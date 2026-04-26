using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NickERP.Platform.Tenancy.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial_AddTenancySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tenancy");

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BillingPlan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "internal"),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "Africa/Accra"),
                    Locale = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "en-GH"),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "GHS"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "tenancy",
                table: "tenants",
                columns: new[] { "Id", "BillingPlan", "Code", "CreatedAt", "Currency", "IsActive", "Locale", "Name", "TimeZone" },
                values: new object[] { 1L, "internal", "nick-tc-scan", new DateTimeOffset(new DateTime(2026, 4, 26, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "GHS", true, "en-GH", "Nick TC-Scan Operations", "Africa/Accra" });

            migrationBuilder.CreateIndex(
                name: "ux_tenants_code",
                schema: "tenancy",
                table: "tenants",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenants",
                schema: "tenancy");
        }
    }
}
