using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Allows generic queue hosts to claim and mutate inspection queue rows
    /// under the system sentinel while keeping tenant work execution scoped
    /// to the row's concrete tenant.
    /// </summary>
    [DbContext(typeof(InspectionDbContext))]
    [Migration("20260513143100_Allow_System_Context_Inspection_Queue_Operations")]
    public partial class Allow_System_Context_Inspection_Queue_Operations : Migration
    {
        private static readonly string[] QueueTables =
        {
            "queue_split_detection",
            "queue_image_analysis",
            "queue_decision_agent",
            "queue_audit_assignment",
            "queue_audit_review",
            "queue_submission"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            foreach (var table in QueueTables)
            {
                migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_isolation_{table} ON inspection.{table};");
                migrationBuilder.Sql($@"
CREATE POLICY tenant_isolation_{table} ON inspection.{table}
    USING (
        (""TenantId"" > 0 AND ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
        OR (COALESCE(current_setting('app.tenant_id', true), '0') = '-1' AND ""TenantId"" > 0)
    )
    WITH CHECK (
        (""TenantId"" > 0 AND ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
        OR (COALESCE(current_setting('app.tenant_id', true), '0') = '-1' AND ""TenantId"" > 0)
    );");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            foreach (var table in QueueTables)
            {
                migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_isolation_{table} ON inspection.{table};");
                migrationBuilder.Sql($@"
CREATE POLICY tenant_isolation_{table} ON inspection.{table}
    USING (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
    WITH CHECK (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
            }
        }
    }
}
