using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// §6.5.4 bootstrap — stamp a <c>version=0</c>
    /// <c>ScannerThresholdProfile</c> row per existing
    /// <c>ScannerDeviceInstance</c> with the v1 hardcoded constants
    /// (<c>canny_low=50</c>, <c>canny_high=150</c>,
    /// <c>percentile_low=0.5</c>, <c>percentile_high=99.5</c>,
    /// <c>split_disagreement_guard_px=50</c>,
    /// <c>pending_without_images_hours=72</c>,
    /// <c>max_image_dim_px=16384</c>). Status <c>active</c>,
    /// <c>proposed_by='bootstrap'</c>,
    /// <c>proposal_rationale.source='v1_hardcoded_values_2026_04_28'</c>.
    ///
    /// <para>
    /// Idempotent — the <c>WHERE NOT EXISTS</c> guard skips scanners
    /// that already have a <c>version=0</c> profile, so re-running the
    /// migration after a partial deploy is safe. Future
    /// <c>ScannerDeviceInstance</c> rows added after this migration
    /// runs do NOT auto-bootstrap; the host must seed them via the
    /// admin UI or a follow-up migration. (Auto-bootstrap on insert is
    /// out of scope for Team A; tracked under §6.5.4 if a fleet
    /// expansion makes it worth automating.)
    /// </para>
    ///
    /// <para>
    /// Status enum maps to integers per the entity:
    /// <c>Proposed=0</c>, <c>Shadow=10</c>, <c>Active=20</c>,
    /// <c>Superseded=30</c>, <c>Rejected=40</c>. Source enum:
    /// <c>Bootstrap=0</c>, <c>AutoTune=10</c>, <c>Manual=20</c>.
    /// </para>
    ///
    /// <para>
    /// <b>RLS note.</b> The
    /// <c>tenant_isolation_scanner_threshold_profiles</c> policy is
    /// installed by the R3 migration with <c>FORCE ROW LEVEL
    /// SECURITY</c>. Migrations execute as the postgres role
    /// (superuser, BYPASSRLS) so the INSERT here lands without policy
    /// interference; nscim_app reads/writes are RLS-narrowed by
    /// <c>app.tenant_id</c> at runtime.
    /// </para>
    /// </summary>
    public partial class BootstrapScannerThresholdProfilesV0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // gen_random_uuid() ships with pgcrypto; nickerp_inspection's
            // schema already calls it elsewhere (see Initial migration's
            // default-value definitions). Versioning the UUID generator
            // explicitly rather than relying on the EF default makes the
            // intent obvious to a reviewer.
            migrationBuilder.Sql(@"
INSERT INTO inspection.scanner_threshold_profiles (
    ""Id"",
    ""ScannerDeviceInstanceId"",
    ""Version"",
    ""ValuesJson"",
    ""Status"",
    ""EffectiveFrom"",
    ""ProposedBy"",
    ""ProposalRationaleJson"",
    ""CreatedAt"",
    ""UpdatedAt"",
    ""TenantId""
)
SELECT
    gen_random_uuid(),
    s.""Id"",
    0,
    jsonb_build_object(
        'edge_detection',  jsonb_build_object('canny_low', 50, 'canny_high', 150),
        'normalization',   jsonb_build_object('percentile_low', 0.5, 'percentile_high', 99.5),
        'split_consensus', jsonb_build_object('disagreement_guard_px', 50),
        'watchdogs',       jsonb_build_object('pending_without_images_hours', 72),
        'decoder_limits',  jsonb_build_object('max_image_dim_px', 16384)
    ),
    20,                          -- ScannerThresholdProfileStatus.Active
    CURRENT_TIMESTAMP,
    0,                           -- ScannerThresholdProposalSource.Bootstrap
    jsonb_build_object('source', 'v1_hardcoded_values_2026_04_28'),
    CURRENT_TIMESTAMP,
    CURRENT_TIMESTAMP,
    s.""TenantId""
FROM inspection.scanner_device_instances s
WHERE NOT EXISTS (
    SELECT 1
    FROM inspection.scanner_threshold_profiles p
    WHERE p.""ScannerDeviceInstanceId"" = s.""Id""
      AND p.""Version"" = 0
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Bootstrap rollback drops the v0 rows so the next bootstrap
            // re-stamps cleanly. Newer profiles (Version > 0) are left
            // untouched — admin-authored or auto-tune rows are not
            // considered ours to delete.
            migrationBuilder.Sql(@"
DELETE FROM inspection.scanner_threshold_profiles
 WHERE ""Version"" = 0
   AND ""ProposedBy"" = 0
   AND ""ProposalRationaleJson"" ->> 'source' = 'v1_hardcoded_values_2026_04_28';");
        }
    }
}
