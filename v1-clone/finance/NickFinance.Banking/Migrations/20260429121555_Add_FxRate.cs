using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.Banking.Migrations
{
    /// <inheritdoc />
    public partial class Add_FxRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "fx_rates",
                schema: "banking",
                columns: table => new
                {
                    fx_rate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    to_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    provider_payload_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fx_rates", x => x.fx_rate_id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_fx_rates_tenant_pair_date",
                schema: "banking",
                table: "fx_rates",
                columns: new[] { "tenant_id", "from_currency", "to_currency", "as_of_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fx_rates_lookup",
                schema: "banking",
                table: "fx_rates",
                columns: new[] { "tenant_id", "from_currency", "to_currency", "as_of_date", "rate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "fx_rates",
                schema: "banking");
        }
    }
}
