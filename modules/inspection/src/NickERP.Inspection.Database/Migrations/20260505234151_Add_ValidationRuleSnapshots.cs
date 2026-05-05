using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 48 / Phase B — FU-validation-rule-evaluation-snapshot.
    ///
    /// Adds <c>inspection.validation_rule_snapshots</c> — an append-only
    /// per-(case, rule) snapshot table written by the
    /// <c>ValidationEngine</c> on every evaluation. Lets <c>/cases/{id}</c>
    /// hydrate the validation pane on cold reload without re-running the
    /// engine.
    ///
    /// <para>
    /// <b>Append-only.</b> No UPDATE; no DELETE. <c>nscim_app</c> gets
    /// SELECT + INSERT (mirrors the DML-permission pattern from
    /// <c>20260427221059_Add_NscimAppRole_Grants</c>); UPDATE / DELETE
    /// remain on the <c>postgres</c> superuser only.
    /// </para>
    ///
    /// <para>
    /// <b>RLS posture.</b> ENABLE + FORCE ROW LEVEL SECURITY +
    /// COALESCE-fail-closed default of <c>'0'</c> (matches the pattern
    /// from <c>20260428104221_AddRuleEvaluations</c>).
    /// </para>
    /// </summary>
    public partial class Add_ValidationRuleSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "validation_rule_snapshots",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PropertiesJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_rule_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_validation_snap_tenant_case_at",
                schema: "inspection",
                table: "validation_rule_snapshots",
                columns: new[] { "TenantId", "CaseId", "EvaluatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_validation_snap_tenant_rule_at",
                schema: "inspection",
                table: "validation_rule_snapshots",
                columns: new[] { "TenantId", "RuleId", "EvaluatedAt" },
                descending: new[] { false, false, true });

            // Phase F1 parity — enable + FORCE-enable RLS and install the
            // tenant-isolation policy. Without this the new table would
            // be a hole in the tenancy posture.
            migrationBuilder.Sql("ALTER TABLE inspection.validation_rule_snapshots ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.validation_rule_snapshots FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_validation_rule_snapshots ON inspection.validation_rule_snapshots "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");

            // Sprint 48 / Phase B — append-only grants. Snapshots are
            // never UPDATEd and never DELETEd, so nscim_app gets SELECT +
            // INSERT only. UPDATE / DELETE rights stay with postgres
            // superuser; if a future operator-driven retention sweep
            // needs to trim history that's a separate migration with
            // explicit user confirmation.
            migrationBuilder.Sql(
                "DO $$ BEGIN "
                + "  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app') THEN "
                + "    GRANT SELECT, INSERT ON TABLE inspection.validation_rule_snapshots TO nscim_app; "
                + "  END IF; "
                + "END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DO $$ BEGIN "
                + "  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app') THEN "
                + "    REVOKE SELECT, INSERT ON TABLE inspection.validation_rule_snapshots FROM nscim_app; "
                + "  END IF; "
                + "END $$;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation_validation_rule_snapshots ON inspection.validation_rule_snapshots;");
            migrationBuilder.Sql("ALTER TABLE inspection.validation_rule_snapshots NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.validation_rule_snapshots DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "validation_rule_snapshots",
                schema: "inspection");
        }
    }
}
