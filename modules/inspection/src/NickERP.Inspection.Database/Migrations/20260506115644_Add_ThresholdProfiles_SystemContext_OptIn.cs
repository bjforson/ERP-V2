using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 59 — opt <c>inspection.scanner_threshold_profiles</c> in to
    /// the system-context sentinel pushed by
    /// <see cref="NickERP.Platform.Tenancy.TenantConnectionInterceptor"/>
    /// when <see cref="NickERP.Platform.Tenancy.ITenantContext.IsSystem"/>
    /// is <c>true</c>.
    ///
    /// <para>
    /// <b>Why.</b> Sprint 12 / Phase R3 created the table under the
    /// strict per-tenant policy (see
    /// <c>20260429062458_Add_PhaseR3_TablesInferenceModernization</c>),
    /// but Sprint 12 also shipped <c>ScannerThresholdResolver</c>
    /// (a singleton, LISTEN/NOTIFY-driven cache; see
    /// <c>modules/inspection/src/NickERP.Inspection.Application/Thresholds/ScannerThresholdResolver.cs</c>)
    /// whose hot-path <c>LoadFromDbAsync</c> calls
    /// <c>tenant.SetSystemContext()</c> to flip
    /// <c>app.tenant_id = '-1'</c>. Without the matching opt-in clause
    /// on the policy USING test, the SELECT returned zero rows for every
    /// cache miss and the resolver fell through to
    /// <c>ScannerThresholdSnapshot.V1Defaults()</c> on every call.
    /// Operator-tuned threshold profiles were silently invisible to the
    /// resolver. The gap was surfaced by the Sprint 57 system-context
    /// audit register sweep (see
    /// <c>docs/system-context-audit-register.md</c>, "Pending opt-in"
    /// section, now resolved by this migration).
    /// </para>
    ///
    /// <para>
    /// <b>What.</b> Drops + recreates
    /// <c>tenant_isolation_scanner_threshold_profiles</c> with the
    /// established opt-in shape used by <c>audit.events</c>,
    /// <c>nickfinance.fx_rate</c>, <c>audit.edge_node_api_keys</c>,
    /// <c>identity.invite_tokens</c>, <c>audit.notifications</c>:
    ///   <c>"TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint
    ///       OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'</c>.
    /// Posture is filling a gap (the resolver's intent already required
    /// the opt-in), not weakening — see
    /// <c>feedback_confirm_before_weakening_security.md</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Audit register.</b>
    /// <c>docs/system-context-audit-register.md</c> records the
    /// <c>ScannerThresholdResolver.LoadFromDbAsync</c> caller and adds
    /// <c>inspection.scanner_threshold_profiles</c> to the
    /// "Tables that opt in to system context" list as part of this
    /// sprint.
    /// </para>
    ///
    /// <para>
    /// <b>Down.</b> Restores the strict per-tenant-only shape (no
    /// <c>'-1'</c> disjunct), matching what
    /// <c>20260429062458_Add_PhaseR3_TablesInferenceModernization</c>
    /// originally installed.
    /// </para>
    /// </summary>
    public partial class Add_ThresholdProfiles_SystemContext_OptIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_scanner_threshold_profiles "
                + "ON inspection.scanner_threshold_profiles;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_scanner_threshold_profiles "
                + "ON inspection.scanner_threshold_profiles "
                + "USING ("
                + "  \"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                + "  OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'"
                + ") "
                + "WITH CHECK ("
                + "  \"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                + "  OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1'"
                + ");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the Phase R3 strict per-tenant shape (no '-1' disjunct).
            // Rolling back this migration re-asserts the bug — the resolver
            // would resume falling back to v1 defaults — but the policy
            // itself is well-formed and matches every other tenanted
            // inspection-schema table created in Phase R3.
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_scanner_threshold_profiles "
                + "ON inspection.scanner_threshold_profiles;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_scanner_threshold_profiles "
                + "ON inspection.scanner_threshold_profiles "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
        }
    }
}
