using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Platform.Audit.Database.Migrations
{
    /// <summary>
    /// Sprint 8 P3 — projection of <c>audit.events</c> into a user-scoped
    /// inbox (<c>audit.notifications</c>) plus a single-row-per-projector
    /// bookmark table (<c>audit.projection_checkpoints</c>).
    ///
    /// <para>
    /// Adds:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>audit.notifications</c> with a unique
    ///         <c>(UserId, EventId)</c> index for projector idempotency, a
    ///         partial unread-only index for the inbox query, and a
    ///         cascade FK to <c>audit.events.EventId</c>.</description></item>
    ///   <item><description><c>audit.projection_checkpoints</c> — one row
    ///         per projector name, no tenant payload, no RLS.</description></item>
    ///   <item><description>Tenant-isolation RLS on
    ///         <c>audit.notifications</c> (mirrors the F1 pattern used on
    ///         <c>audit.events</c>). User-isolation is enforced at the LINQ
    ///         layer in <c>NotificationsEndpoints</c> because there is no
    ///         <c>app.user_id</c> session setting plumbed today
    ///         (Sprint 2 / H2 set up tenant-context only).</description></item>
    ///   <item><description>Role grants for <c>nscim_app</c>: SELECT,
    ///         INSERT, UPDATE on <c>audit.notifications</c> (UPDATE is
    ///         needed for mark-as-read; DELETE remains withheld). SELECT,
    ///         INSERT, UPDATE on <c>audit.projection_checkpoints</c>
    ///         (the projector upserts).</description></item>
    /// </list>
    /// </summary>
    public partial class Add_Notifications_And_ProjectionCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Link = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notifications_events_EventId",
                        column: x => x.EventId,
                        principalSchema: "audit",
                        principalTable: "events",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "projection_checkpoints",
                schema: "audit",
                columns: table => new
                {
                    ProjectionName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastIngestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projection_checkpoints", x => x.ProjectionName);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_EventId",
                schema: "audit",
                table: "notifications",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_unread",
                schema: "audit",
                table: "notifications",
                columns: new[] { "UserId", "TenantId" },
                filter: "\"ReadAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_user_event",
                schema: "audit",
                table: "notifications",
                columns: new[] { "UserId", "EventId" },
                unique: true);

            // ------------------------------------------------------------
            // Sprint 8 P3 — RLS on audit.notifications. Mirrors the F1
            // tenant_isolation_events pattern on audit.events: ENABLE +
            // FORCE so even superuser writes go through WITH CHECK, plus a
            // policy that filters on the session-local app.tenant_id pushed
            // by TenantConnectionInterceptor. COALESCE → '0' fail-closed
            // default if the session value is unset (matches F1 invariant).
            //
            // User-isolation is NOT enforced here at the DB layer because
            // there is no app.user_id session setting today; the LINQ layer
            // in NotificationsEndpoints filters on UserId = current-user
            // and the unique (UserId, EventId) index makes accidental
            // cross-user inserts a violation rather than data loss.
            // Documented as a TODO in the entity class so a future Identity
            // sprint can promote it to a DB-layer policy.
            // ------------------------------------------------------------
            migrationBuilder.Sql(
                "ALTER TABLE audit.notifications ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE audit.notifications FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_notifications ON audit.notifications "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");

            // ------------------------------------------------------------
            // Role grants. nscim_app gets SELECT + INSERT + UPDATE on
            // audit.notifications (UPDATE for mark-as-read). DELETE stays
            // withheld so users can't purge their own history. The
            // sequence-default-privileges from Add_NscimAppRole_Grants
            // already cover any future sequences in the audit schema.
            //
            // The default ALTER DEFAULT PRIVILEGES set up in
            // Add_NscimAppRole_Grants only grants SELECT + INSERT for
            // future tables — UPDATE on audit.notifications must be added
            // explicitly here. We intentionally do NOT change the schema
            // default to include UPDATE (that would loosen the
            // append-only posture for any future audit table).
            // ------------------------------------------------------------
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE ON audit.notifications TO nscim_app;");
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT, UPDATE ON audit.projection_checkpoints TO nscim_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order: revoke grants → drop policy → drop tables.
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE ON audit.projection_checkpoints FROM nscim_app;");
            migrationBuilder.Sql(
                "REVOKE SELECT, INSERT, UPDATE ON audit.notifications FROM nscim_app;");

            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS tenant_isolation_notifications ON audit.notifications;");
            migrationBuilder.Sql(
                "ALTER TABLE audit.notifications NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE audit.notifications DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "projection_checkpoints",
                schema: "audit");
        }
    }
}
