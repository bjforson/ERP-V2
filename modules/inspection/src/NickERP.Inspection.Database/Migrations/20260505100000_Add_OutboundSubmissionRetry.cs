using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 36 / FU-outbound-dispatch-retry — extends
    /// <c>inspection.outbound_submissions</c> with the bounded-retry
    /// scaffolding the dispatch worker needs: <c>RetryCount</c> (int,
    /// default 0) + <c>NextAttemptAt</c> (timestamp, nullable).
    ///
    /// <para>
    /// Pre-Sprint-36, transient adapter failures (network blips,
    /// authority HTTP 5xx) flipped the row to <c>Status='error'</c> and
    /// required an operator requeue. This migration adds the columns
    /// the dispatcher reads/writes to schedule exponential backoff
    /// retries instead.
    /// </para>
    ///
    /// <para>
    /// Backfill: existing rows take <c>RetryCount = 0</c> (default,
    /// applied by the non-nullable column default) and
    /// <c>NextAttemptAt = NULL</c>. The dispatcher's pickup query
    /// treats NULL as "eligible immediately" so the production queue
    /// keeps moving without operator action.
    /// </para>
    ///
    /// <para>
    /// Also adds the composite index
    /// <c>ix_outbound_tenant_status_next_attempt</c> on
    /// <c>(TenantId, Status, NextAttemptAt)</c> — supports the new
    /// pickup-query filter <c>Status='pending' AND
    /// (NextAttemptAt IS NULL OR NextAttemptAt &lt;= now())</c> without
    /// scanning the existing priority-ordered index.
    /// </para>
    /// </summary>
    public partial class Add_OutboundSubmissionRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                schema: "inspection",
                table: "outbound_submissions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                schema: "inspection",
                table: "outbound_submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_tenant_status_next_attempt",
                schema: "inspection",
                table: "outbound_submissions",
                columns: new[] { "TenantId", "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbound_tenant_status_next_attempt",
                schema: "inspection",
                table: "outbound_submissions");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                schema: "inspection",
                table: "outbound_submissions");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                schema: "inspection",
                table: "outbound_submissions");
        }
    }
}
