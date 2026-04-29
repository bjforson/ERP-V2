using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.NickFinance.Database.Migrations
{
    /// <inheritdoc />
    public partial class Init_NickFinance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nickfinance");

            migrationBuilder.CreateTable(
                name: "fx_rate",
                schema: "nickfinance",
                columns: table => new
                {
                    FromCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ToCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "date", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    Rate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    PublishedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fx_rate", x => new { x.FromCurrency, x.ToCurrency, x.EffectiveDate });
                    table.CheckConstraint("ck_fx_rate_positive", "\"Rate\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "petty_cash_boxes",
                schema: "nickfinance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CustodianUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApproverUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpeningBalanceAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false, defaultValue: 0m),
                    OpeningBalanceCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_petty_cash_boxes", x => x.Id);
                    table.CheckConstraint("ck_petty_cash_boxes_custodian_neq_approver", "\"CustodianUserId\" <> \"ApproverUserId\"");
                    table.CheckConstraint("ck_petty_cash_boxes_opening_currency_match", "\"OpeningBalanceCurrency\" = \"CurrencyCode\"");
                });

            migrationBuilder.CreateTable(
                name: "petty_cash_ledger_events",
                schema: "nickfinance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoxId = table.Column<Guid>(type: "uuid", nullable: false),
                    VoucherId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    AmountNative = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CurrencyNative = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AmountBase = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CurrencyBase = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    FxRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    FxRateDate = table.Column<DateTime>(type: "date", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PostedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrectsEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_petty_cash_ledger_events", x => x.Id);
                    table.CheckConstraint("ck_ledger_amount_base_nonneg", "\"AmountBase\" >= 0");
                    table.CheckConstraint("ck_ledger_amount_native_nonneg", "\"AmountNative\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "petty_cash_periods",
                schema: "nickfinance",
                columns: table => new
                {
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    PeriodYearMonth = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_petty_cash_periods", x => new { x.TenantId, x.PeriodYearMonth });
                });

            migrationBuilder.CreateTable(
                name: "petty_cash_vouchers",
                schema: "nickfinance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RequestedAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RequestedCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RequestedAmountBase = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    RequestedCurrencyBase = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ApproverUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisbursedAmount = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    DisbursedCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    DisbursedAmountBase = table.Column<decimal>(type: "numeric(18,4)", nullable: true),
                    DisbursedCurrencyBase = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    DisbursedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReconciledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_petty_cash_vouchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_petty_cash_vouchers_petty_cash_boxes_BoxId",
                        column: x => x.BoxId,
                        principalSchema: "nickfinance",
                        principalTable: "petty_cash_boxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fx_rate_pair_effective_desc",
                schema: "nickfinance",
                table: "fx_rate",
                columns: new[] { "FromCurrency", "ToCurrency", "EffectiveDate" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_petty_cash_boxes_tenant",
                schema: "nickfinance",
                table: "petty_cash_boxes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_petty_cash_boxes_tenant_code",
                schema: "nickfinance",
                table: "petty_cash_boxes",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_corrects",
                schema: "nickfinance",
                table: "petty_cash_ledger_events",
                column: "CorrectsEventId");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_tenant_box_posted",
                schema: "nickfinance",
                table: "petty_cash_ledger_events",
                columns: new[] { "TenantId", "BoxId", "PostedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_voucher",
                schema: "nickfinance",
                table: "petty_cash_ledger_events",
                column: "VoucherId");

            migrationBuilder.CreateIndex(
                name: "IX_petty_cash_vouchers_BoxId",
                schema: "nickfinance",
                table: "petty_cash_vouchers",
                column: "BoxId");

            migrationBuilder.CreateIndex(
                name: "ix_petty_cash_vouchers_requester",
                schema: "nickfinance",
                table: "petty_cash_vouchers",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_petty_cash_vouchers_tenant_box_state",
                schema: "nickfinance",
                table: "petty_cash_vouchers",
                columns: new[] { "TenantId", "BoxId", "State" });

            migrationBuilder.CreateIndex(
                name: "ux_petty_cash_vouchers_tenant_box_seq",
                schema: "nickfinance",
                table: "petty_cash_vouchers",
                columns: new[] { "TenantId", "BoxId", "SequenceNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fx_rate",
                schema: "nickfinance");

            migrationBuilder.DropTable(
                name: "petty_cash_ledger_events",
                schema: "nickfinance");

            migrationBuilder.DropTable(
                name: "petty_cash_periods",
                schema: "nickfinance");

            migrationBuilder.DropTable(
                name: "petty_cash_vouchers",
                schema: "nickfinance");

            migrationBuilder.DropTable(
                name: "petty_cash_boxes",
                schema: "nickfinance");
        }
    }
}
