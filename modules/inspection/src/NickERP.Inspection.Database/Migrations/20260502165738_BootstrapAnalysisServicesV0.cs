using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// VP6 Phase A bootstrap — stamp the immutable
    /// <c>IsBuiltInAllLocations = TRUE</c> "All Locations"
    /// <c>AnalysisService</c> per existing tenant, and auto-join every
    /// existing <c>Location</c> to its tenant's "All Locations" service
    /// via <c>analysis_service_locations</c>.
    ///
    /// <para>
    /// Tenant discovery happens via DISTINCT <c>TenantId</c> from
    /// <c>inspection.locations</c> and <c>inspection.cases</c> — this
    /// migration runs against <c>nickerp_inspection</c> and cannot query
    /// the canonical <c>nickerp_platform.tenancy.tenants</c> table
    /// across-DB. Tenants without any inspection-side data (no
    /// locations, no cases) won't get a row from this bootstrap; their
    /// "All Locations" service will be created by the runtime
    /// auto-bootstrap hook (Phase A.5) when their first location lands.
    /// </para>
    ///
    /// <para>
    /// **Idempotent.** The two <c>WHERE NOT EXISTS</c> guards skip
    /// tenants that already have an <c>IsBuiltInAllLocations</c>
    /// service, and locations already joined to it. Safe to re-run.
    /// </para>
    ///
    /// <para>
    /// **RLS note.** Migrations execute as the postgres role (superuser,
    /// BYPASSRLS) so the INSERTs land cross-tenant without
    /// <c>tenant_isolation_*</c> policy interference. nscim_app
    /// reads/writes are RLS-narrowed at runtime.
    /// </para>
    ///
    /// <para>
    /// **Single-host dev** typically has no locations yet at sprint
    /// time — this migration is expected to insert zero rows there,
    /// which is fine. The shape exists so production / staging deploys
    /// catch up automatically.
    /// </para>
    /// </summary>
    public partial class BootstrapAnalysisServicesV0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: stamp the "All Locations" AnalysisService per
            // distinct TenantId discovered in the inspection module's
            // own tables. The WHERE NOT EXISTS guard makes this
            // idempotent.
            migrationBuilder.Sql(@"
INSERT INTO inspection.analysis_services (
    ""Id"",
    ""Name"",
    ""Description"",
    ""IsBuiltInAllLocations"",
    ""CreatedAt"",
    ""CreatedByUserId"",
    ""UpdatedAt"",
    ""TenantId""
)
SELECT
    gen_random_uuid(),
    'All Locations',
    'Built-in service that includes every location in the tenant. Cannot be deleted; admins manage analyst access via membership.',
    TRUE,
    CURRENT_TIMESTAMP,
    NULL,
    CURRENT_TIMESTAMP,
    t.""TenantId""
FROM (
    SELECT DISTINCT ""TenantId"" FROM inspection.locations
    UNION
    SELECT DISTINCT ""TenantId"" FROM inspection.cases
) t
WHERE NOT EXISTS (
    SELECT 1
    FROM inspection.analysis_services s
    WHERE s.""TenantId"" = t.""TenantId""
      AND s.""IsBuiltInAllLocations"" = TRUE
);
");

            // Step 2: auto-join every existing location to its tenant's
            // "All Locations" service. Idempotent via the composite PK
            // (AnalysisServiceId, LocationId) + an explicit NOT EXISTS
            // for clarity.
            migrationBuilder.Sql(@"
INSERT INTO inspection.analysis_service_locations (
    ""AnalysisServiceId"",
    ""LocationId"",
    ""AddedAt"",
    ""TenantId""
)
SELECT
    s.""Id"",
    l.""Id"",
    CURRENT_TIMESTAMP,
    l.""TenantId""
FROM inspection.locations l
JOIN inspection.analysis_services s
    ON s.""TenantId"" = l.""TenantId""
   AND s.""IsBuiltInAllLocations"" = TRUE
WHERE NOT EXISTS (
    SELECT 1
    FROM inspection.analysis_service_locations asl
    WHERE asl.""AnalysisServiceId"" = s.""Id""
      AND asl.""LocationId"" = l.""Id""
);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversal: drop the auto-joined memberships and the "All
            // Locations" services we created. The trigger on
            // analysis_services would normally reject these deletes — we
            // have to disable it, do the cleanup, then re-enable.
            migrationBuilder.Sql("ALTER TABLE inspection.analysis_services DISABLE TRIGGER trg_prevent_built_in_all_locations_delete;");

            migrationBuilder.Sql(@"
DELETE FROM inspection.analysis_service_locations asl
USING inspection.analysis_services s
WHERE asl.""AnalysisServiceId"" = s.""Id""
  AND s.""IsBuiltInAllLocations"" = TRUE;");

            migrationBuilder.Sql(@"
DELETE FROM inspection.analysis_services
WHERE ""IsBuiltInAllLocations"" = TRUE;");

            migrationBuilder.Sql("ALTER TABLE inspection.analysis_services ENABLE TRIGGER trg_prevent_built_in_all_locations_delete;");
        }
    }
}
