using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Queueing.Database.Migrations
{
    /// <summary>
    /// Allows the queueing background services to claim, release, and sweep
    /// positive-tenant queue rows under the system sentinel. Consumers still
    /// switch back to the row's concrete tenant before executing module code.
    /// </summary>
    [DbContext(typeof(QueueingDbContext))]
    [Migration("20260513143000_Allow_System_Context_Queue_Operations")]
    public partial class Allow_System_Context_Queue_Operations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            foreach (var table in new[] { "outbox", "dead_letter" })
            {
                migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_isolation_{table} ON queueing.{table};");
                migrationBuilder.Sql($@"
CREATE POLICY tenant_isolation_{table} ON queueing.{table}
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

            foreach (var table in new[] { "outbox", "dead_letter" })
            {
                migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_isolation_{table} ON queueing.{table};");
                migrationBuilder.Sql($@"
CREATE POLICY tenant_isolation_{table} ON queueing.{table}
    USING (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
    WITH CHECK (""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
            }
        }
    }
}
