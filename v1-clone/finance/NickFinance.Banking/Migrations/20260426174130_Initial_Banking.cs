using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickFinance.Banking.Migrations
{
    /// <inheritdoc />
    public partial class Initial_Banking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "banking");

            migrationBuilder.CreateTable(
                name: "bank_accounts",
                schema: "banking",
                columns: table => new
                {
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    bank_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    account_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ledger_account = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_accounts", x => x.bank_account_id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliations",
                schema: "banking",
                columns: table => new
                {
                    reconciliation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                    bank_balance_minor = table.Column<long>(type: "bigint", nullable: false),
                    ledger_balance_minor = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    opened_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliations", x => x.reconciliation_id);
                });

            migrationBuilder.CreateTable(
                name: "statements",
                schema: "banking",
                columns: table => new
                {
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    opening_balance_minor = table.Column<long>(type: "bigint", nullable: false),
                    closing_balance_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source_file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    parser_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_statements", x => x.statement_id);
                    table.ForeignKey(
                        name: "FK_statements_bank_accounts_bank_account_id",
                        column: x => x.bank_account_id,
                        principalSchema: "banking",
                        principalTable: "bank_accounts",
                        principalColumn: "bank_account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                schema: "banking",
                columns: table => new
                {
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_date = table.Column<DateOnly>(type: "date", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    direction = table.Column<short>(type: "smallint", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    match_status = table.Column<short>(type: "smallint", nullable: false),
                    matched_to_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    matched_to_entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    matched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    matched_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transactions", x => x.transaction_id);
                    table.ForeignKey(
                        name: "FK_transactions_statements_statement_id",
                        column: x => x.statement_id,
                        principalSchema: "banking",
                        principalTable: "statements",
                        principalColumn: "statement_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_bank_accounts_tenant_code",
                schema: "banking",
                table: "bank_accounts",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reconciliations_account_date",
                schema: "banking",
                table: "reconciliations",
                columns: new[] { "bank_account_id", "as_of_date" });

            migrationBuilder.CreateIndex(
                name: "ix_statements_account_period",
                schema: "banking",
                table: "statements",
                columns: new[] { "bank_account_id", "period_start", "period_end" });

            migrationBuilder.CreateIndex(
                name: "ix_transactions_account_date",
                schema: "banking",
                table: "transactions",
                columns: new[] { "bank_account_id", "transaction_date" });

            migrationBuilder.CreateIndex(
                name: "IX_transactions_statement_id",
                schema: "banking",
                table: "transactions",
                column: "statement_id");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_tenant_status",
                schema: "banking",
                table: "transactions",
                columns: new[] { "tenant_id", "match_status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reconciliations",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "transactions",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "statements",
                schema: "banking");

            migrationBuilder.DropTable(
                name: "bank_accounts",
                schema: "banking");
        }
    }
}
