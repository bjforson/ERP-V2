using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.Banking.Migrations
{
    /// <inheritdoc />
    public partial class Add_FxRevaluationLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "fx_revaluation_log",
                schema: "banking",
                columns: table => new
                {
                    log_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false),
                    gl_account = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    revalued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rate_used = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    balance_minor = table.Column<long>(type: "bigint", nullable: false),
                    ledger_event_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fx_revaluation_log", x => x.log_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fx_revaluation_log_lookup",
                schema: "banking",
                table: "fx_revaluation_log",
                columns: new[] { "tenant_id", "gl_account", "currency_code", "as_of_date" });

            migrationBuilder.CreateIndex(
                name: "ux_fx_revaluation_log_tenant_account_ccy_period",
                schema: "banking",
                table: "fx_revaluation_log",
                columns: new[] { "tenant_id", "gl_account", "currency_code", "period_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "fx_revaluation_log",
                schema: "banking");
        }
    }
}
