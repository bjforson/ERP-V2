using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Sprint 9 / FU-userid — promote <c>audit.notifications</c> user-isolation
    /// from a LINQ-level filter to a DB-level RLS policy that combines
    /// tenant + user isolation.
    ///
    /// <para>
    /// Sprint 8 P3 shipped notifications with a tenant-only RLS policy
    /// (<c>tenant_isolation_notifications</c>) and a LINQ
    /// <c>WHERE n.UserId == currentUser.Id</c> guard at the endpoint
    /// layer. The unique <c>(UserId, EventId)</c> index made cross-user
    /// inserts noisy rather than silent, but there was no DB-enforced
    /// suspenders. FU-userid plumbs <c>app.user_id</c> through the
    /// <see cref="NickERP.Platform.Tenancy.TenantConnectionInterceptor"/>
    /// (mirroring the existing <c>app.tenant_id</c> plumbing) so the
    /// policy can compare <c>"UserId"</c> against the session value.
    /// </para>
    ///
    /// <para>
    /// Drops the old policy and creates a combined policy
    /// (<c>tenant_user_isolation_notifications</c>) that filters on BOTH
    /// <c>"TenantId"</c> AND <c>"UserId"</c>. Includes a system-context
    /// OR clause (mirroring Sprint 5 / G1-3 on <c>audit.events</c>) so
    /// the <c>AuditNotificationProjector</c> can INSERT rows under
    /// <see cref="NickERP.Platform.Tenancy.ITenantContext.SetSystemContext"/>
    /// — its writes carry real (tenant, user) pairs but the system
    /// context's <c>app.user_id</c> resolves to the zero UUID, so
    /// without the OR clause the WITH CHECK would block the INSERT.
    /// Reads stay user-scoped because production code paths don't read
    /// notifications under system context (the projector only reads
    /// <c>audit.events</c>).
    /// </para>
    ///
    /// <para>
    /// GRANTs are unchanged — <c>nscim_app</c> still has SELECT, INSERT,
    /// UPDATE on <c>audit.notifications</c>; DELETE remains withheld so
    /// users cannot purge their own history.
    /// </para>
    ///
    /// <para>
    /// Audit register entry — see
    /// <c>docs/system-context-audit-register.md</c>; the projector's
    /// per-tenant insert path is now explicitly registered as a
    /// <c>SetSystemContext()</c> caller and <c>audit.notifications</c>
    /// is added to the "Tables that opt in" list.
    /// </para>
    /// </summary>
    public partial class Promote_Notifications_UserIsolation_To_Rls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the Sprint-8 P3 tenant-only policy first; CREATE POLICY
            // would otherwise conflict on the same table.
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_notifications ON audit.notifications;");

            // ------------------------------------------------------------
            // New combined policy. USING + WITH CHECK both compare
            // (tenant_id, user_id) against the session-local values, with a
            // system-context OR clause that admits the projector's writes.
            // The COALESCE(NULLIF(...), default) idiom — rather than the
            // bare COALESCE — handles the edge case where the session
            // variable is set to an empty string (e.g. an explicit
            // `SET app.user_id = ''`); without NULLIF, COALESCE would treat
            // '' as a valid value and downstream `::uuid` cast would fail
            // with `invalid input syntax for type uuid: ""`. Mirrors the
            // F1 pattern on audit.events.
            // ------------------------------------------------------------
            migrationBuilder.Sql(
                "CREATE POLICY tenant_user_isolation_notifications ON audit.notifications "
                + "USING ("
                + "(\"TenantId\" = COALESCE(NULLIF(current_setting('app.tenant_id', true), ''), '0')::bigint "
                + "AND \"UserId\" = COALESCE(NULLIF(current_setting('app.user_id', true), ''), '00000000-0000-0000-0000-000000000000')::uuid) "
                + "OR (current_setting('app.tenant_id', true) = '-1')"
                + ") "
                + "WITH CHECK ("
                + "(\"TenantId\" = COALESCE(NULLIF(current_setting('app.tenant_id', true), ''), '0')::bigint "
                + "AND \"UserId\" = COALESCE(NULLIF(current_setting('app.user_id', true), ''), '00000000-0000-0000-0000-000000000000')::uuid) "
                + "OR (current_setting('app.tenant_id', true) = '-1')"
                + ");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse to the Sprint-8 P3 tenant-only shape. Down is meant
            // to be a clean rollback to before this migration applied;
            // user-isolation reverts to LINQ-only enforcement.
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_user_isolation_notifications ON audit.notifications;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_notifications ON audit.notifications "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
        }
    }
}
