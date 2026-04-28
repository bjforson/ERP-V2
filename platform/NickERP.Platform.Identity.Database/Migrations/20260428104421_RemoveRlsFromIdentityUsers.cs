using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Identity.Database.Migrations
{
    /// <summary>
    /// Sprint 2 — H2 Identity-Tenancy Interlock. Carves
    /// <c>identity.identity_users</c> out of <c>FORCE ROW LEVEL SECURITY</c>
    /// (and drops the <c>tenant_isolation_identity_users</c> policy installed
    /// by <see cref="Add_RLS_Policies"/>).
    ///
    /// <para><b>Why this carve-out is necessary.</b>
    /// <c>identity.identity_users</c> is the table that <i>establishes</i>
    /// tenant context: the auth flow
    /// (<see cref="NickERP.Platform.Identity.Database.Services.DbIdentityResolver"/>)
    /// has to read it to discover the calling principal's <c>TenantId</c>
    /// before <see cref="NickERP.Platform.Tenancy.TenantConnectionInterceptor"/>
    /// can push <c>app.tenant_id</c> to Postgres. RLS on this table is
    /// fundamentally circular — under the production posture
    /// (<c>nscim_app</c>, NOBYPASSRLS) the <c>FORCE</c> policy applies,
    /// <c>app.tenant_id</c> is unset, the <c>COALESCE</c> falls through to
    /// <c>'0'</c>, and every row is filtered out → 401 → demo dies.</para>
    ///
    /// <para><b>What still protects tenant data.</b>
    /// <c>identity_users</c> is the <i>only</i> tenanted table NOT under
    /// <c>FORCE ROW LEVEL SECURITY</c>. Every other tenant-owned identity
    /// table (<c>app_scopes</c>, <c>user_scopes</c>, <c>service_token_*</c>)
    /// keeps its policy, as does every domain table in
    /// <c>nickerp_inspection</c>. A code path that reads <c>identity_users</c>
    /// directly cannot reach tenant data through it — joining out hits the
    /// next hop's RLS. The user-discovery hop is the single intentional
    /// carve-out; defense-in-depth holds for the data behind it.</para>
    ///
    /// <para>The matching startup check in <c>DbIdentityResolver</c>'s host
    /// wiring logs <c>IDENTITY-USERS-RLS-RE-ENABLED</c> if a future migration
    /// silently re-enables FORCE RLS on this table.</para>
    /// </summary>
    public partial class RemoveRlsFromIdentityUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the F1 tenant_isolation_identity_users policy, lift FORCE
            // RLS, and DISABLE RLS on the table. All three are needed: with
            // RLS still ENABLED but no policy installed, Postgres applies a
            // default-deny to every non-owner role — which means non-superuser
            // app roles (e.g. nscim_app) see zero rows, exactly the auth-flow
            // failure mode H2 exists to fix. Owner-only access from the
            // postgres superuser would still work, but the production posture
            // is nscim_app, so RLS must be fully OFF on this table.
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_identity_users ON identity.identity_users;");
            migrationBuilder.Sql(
                "ALTER TABLE identity.identity_users NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE identity.identity_users DISABLE ROW LEVEL SECURITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-create exactly the policy F1's Add_RLS_Policies installed
            // (same USING / WITH CHECK clause, same fail-closed COALESCE).
            migrationBuilder.Sql(
                "ALTER TABLE identity.identity_users ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE identity.identity_users FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_identity_users ON identity.identity_users "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
        }
    }
}
