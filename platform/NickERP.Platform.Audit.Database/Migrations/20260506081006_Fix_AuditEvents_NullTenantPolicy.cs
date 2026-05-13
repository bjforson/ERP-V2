using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Tightens the audit.events policy recreated by the partitioning migration.
    /// Regular tenant sessions must not be able to insert NULL-tenant audit
    /// events; only SetSystemContext's app.tenant_id = '-1' sentinel may use
    /// the cross-tenant / suite-wide audit-events opt-in.
    /// </summary>
    [DbContext(typeof(AuditDbContext))]
    [Migration("20260506081006_Fix_AuditEvents_NullTenantPolicy")]
    public partial class Fix_AuditEvents_NullTenantPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP POLICY IF EXISTS tenant_isolation_events ON audit.events;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation_events ON audit.events
    USING (
        ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
    )
    WITH CHECK (
        ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
    );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP POLICY IF EXISTS tenant_isolation_events ON audit.events;");
            migrationBuilder.Sql(@"
CREATE POLICY tenant_isolation_events ON audit.events
    USING (
        ""TenantId"" IS NULL
        OR ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
    )
    WITH CHECK (
        ""TenantId"" IS NULL
        OR ""TenantId"" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
        OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'
    );");
        }
    }
}
