using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint A1 — persist <c>RulesEvaluationResult</c> per
    /// (case, AuthorityCode) so the rules pane survives a page reload.
    /// One row per authority per case; re-evaluation upserts on
    /// <c>(TenantId, CaseId, AuthorityCode)</c> rather than appending
    /// history.
    ///
    /// Also enables + force-enables RLS and installs the
    /// tenant-isolation policy, matching the pattern applied to every
    /// tenant-owned table in F1's <c>Add_RLS_Policies</c>.
    /// </summary>
    public partial class AddRuleEvaluations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rule_evaluations",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorityCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ViolationsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    MutationsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    ProviderErrorsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rule_evaluations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rule_eval_tenant_case_at",
                schema: "inspection",
                table: "rule_evaluations",
                columns: new[] { "TenantId", "CaseId", "EvaluatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_rule_eval_tenant_case_authority",
                schema: "inspection",
                table: "rule_evaluations",
                columns: new[] { "TenantId", "CaseId", "AuthorityCode" },
                unique: true);

            // Phase F1 parity — enable + FORCE-enable RLS and install the
            // tenant-isolation policy. Without this the new table would be
            // a hole in the tenancy posture.
            migrationBuilder.Sql("ALTER TABLE inspection.rule_evaluations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.rule_evaluations FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "CREATE POLICY tenant_isolation_rule_evaluations ON inspection.rule_evaluations "
                + "USING (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) "
                + "WITH CHECK (\"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation_rule_evaluations ON inspection.rule_evaluations;");
            migrationBuilder.Sql("ALTER TABLE inspection.rule_evaluations NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inspection.rule_evaluations DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "rule_evaluations",
                schema: "inspection");
        }
    }
}
