using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.Ledger.Migrations
{
    /// <inheritdoc />
    public partial class Initial_Ledger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "finance");

            migrationBuilder.CreateTable(
                name: "accounting_periods",
                schema: "finance",
                columns: table => new
                {
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fiscal_year = table.Column<int>(type: "integer", nullable: false),
                    month_number = table.Column<byte>(type: "smallint", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounting_periods", x => x.period_id);
                });

            migrationBuilder.CreateTable(
                name: "ledger_events",
                schema: "finance",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_entity_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    reverses_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    narration = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_events", x => x.event_id);
                    table.ForeignKey(
                        name: "FK_ledger_events_accounting_periods_period_id",
                        column: x => x.period_id,
                        principalSchema: "finance",
                        principalTable: "accounting_periods",
                        principalColumn: "period_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_event_lines",
                schema: "finance",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<short>(type: "smallint", nullable: false),
                    account_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    debit_minor = table.Column<long>(type: "bigint", nullable: false),
                    credit_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    cost_center_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    dims_extra = table.Column<string>(type: "jsonb", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_event_lines", x => new { x.event_id, x.line_no });
                    table.ForeignKey(
                        name: "FK_ledger_event_lines_ledger_events_event_id",
                        column: x => x.event_id,
                        principalSchema: "finance",
                        principalTable: "ledger_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounting_periods_tenant_id_fiscal_year_month_number",
                schema: "finance",
                table: "accounting_periods",
                columns: new[] { "tenant_id", "fiscal_year", "month_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_event_lines_account_code_currency_code",
                schema: "finance",
                table: "ledger_event_lines",
                columns: new[] { "account_code", "currency_code" });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_event_lines_site_id",
                schema: "finance",
                table: "ledger_event_lines",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_events_idempotency_key",
                schema: "finance",
                table: "ledger_events",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_events_period_id",
                schema: "finance",
                table: "ledger_events",
                column: "period_id");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_events_reverses_event_id",
                schema: "finance",
                table: "ledger_events",
                column: "reverses_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_events_tenant_id_effective_date",
                schema: "finance",
                table: "ledger_events",
                columns: new[] { "tenant_id", "effective_date" });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_events_tenant_id_period_id",
                schema: "finance",
                table: "ledger_events",
                columns: new[] { "tenant_id", "period_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ledger_event_lines",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "ledger_events",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "accounting_periods",
                schema: "finance");
        }
    }
}
