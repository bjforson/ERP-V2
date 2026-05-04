using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 22 / B2.1 — extends <c>inspection.outbound_submissions</c>
    /// with two operational columns the v1 ICUMS submission queue UI
    /// shows: <c>Priority</c> (int, default 0; higher value = earlier
    /// dispatch) and <c>LastAttemptAt</c> (timestamp, nullable;
    /// last-attempted timestamp distinct from <c>SubmittedAt</c> /
    /// <c>RespondedAt</c>).
    ///
    /// <para>
    /// Also adds a composite index
    /// <c>ix_outbound_tenant_status_priority_time</c> on
    /// <c>(TenantId, Status, Priority DESC, SubmittedAt)</c> — the canonical
    /// "list pending rows highest priority first, oldest within priority"
    /// query the admin queue page hits on every refresh.
    /// </para>
    ///
    /// <para>
    /// Backfill: existing rows take <c>Priority = 0</c> (default) and
    /// <c>LastAttemptAt = NULL</c>. The default is applied by the
    /// non-nullable column default; no explicit UPDATE is needed.
    /// </para>
    /// </summary>
    public partial class Add_OutboundSubmissionPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                schema: "inspection",
                table: "outbound_submissions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAt",
                schema: "inspection",
                table: "outbound_submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_tenant_status_priority_time",
                schema: "inspection",
                table: "outbound_submissions",
                columns: new[] { "TenantId", "Status", "Priority", "SubmittedAt" },
                descending: new[] { false, false, true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbound_tenant_status_priority_time",
                schema: "inspection",
                table: "outbound_submissions");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "inspection",
                table: "outbound_submissions");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                schema: "inspection",
                table: "outbound_submissions");
        }
    }
}
