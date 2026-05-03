using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 14 / VP6 Phase A — adds the four AnalysisService tables
    /// (analysis_services, analysis_service_locations, analysis_service_users,
    /// case_claims) with RLS + FORCE + tenant_isolation_* policies in the
    /// same shape as the Sprint 12 R3 migration. Also installs a database
    /// trigger preventing deletion of any AnalysisService row where
    /// IsBuiltInAllLocations = TRUE — defence-in-depth for the
    /// service-layer guard that the Phase B admin UI will call into.
    ///
    /// VP6 (locked 2026-05-02): N:N location↔service. Every tenant
    /// gets exactly one immutable "All Locations" service per the
    /// unique partial index ux_analysis_services_tenant_built_in.
    /// First-claim-wins under shared visibility via
    /// ux_case_claims_active_per_case (CaseId WHERE ReleasedAt IS NULL).
    /// </summary>
    public partial class Add_AnalysisServiceVp6 : Migration
    {
        /// <summary>Tenanted tables introduced by this migration. RLS-enable + FORCE + tenant_isolation_* policy installed for each.</summary>
        private static readonly string[] TenantedTables = new[]
        {
            "analysis_services",
            "analysis_service_locations",
            "analysis_service_users",
            "case_claims"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analysis_services",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsBuiltInAllLocations = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_services", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "analysis_service_locations",
                schema: "inspection",
                columns: table => new
                {
                    AnalysisServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_service_locations", x => new { x.AnalysisServiceId, x.LocationId });
                    table.ForeignKey(
                        name: "FK_analysis_service_locations_analysis_services_AnalysisServic~",
                        column: x => x.AnalysisServiceId,
                        principalSchema: "inspection",
                        principalTable: "analysis_services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_analysis_service_locations_locations_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "inspection",
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "analysis_service_users",
                schema: "inspection",
                columns: table => new
                {
                    AnalysisServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_service_users", x => new { x.AnalysisServiceId, x.UserId });
                    table.ForeignKey(
                        name: "FK_analysis_service_users_analysis_services_AnalysisServiceId",
                        column: x => x.AnalysisServiceId,
                        principalSchema: "inspection",
                        principalTable: "analysis_services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "case_claims",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_case_claims_analysis_services_AnalysisServiceId",
                        column: x => x.AnalysisServiceId,
                        principalSchema: "inspection",
                        principalTable: "analysis_services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_case_claims_cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "inspection",
                        principalTable: "cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_analysis_service_locations_location",
                schema: "inspection",
                table: "analysis_service_locations",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "ix_analysis_service_locations_tenant",
                schema: "inspection",
                table: "analysis_service_locations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_analysis_service_users_tenant",
                schema: "inspection",
                table: "analysis_service_users",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_analysis_service_users_user",
                schema: "inspection",
                table: "analysis_service_users",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_analysis_services_tenant_built_in",
                schema: "inspection",
                table: "analysis_services",
                column: "TenantId",
                unique: true,
                filter: "\"IsBuiltInAllLocations\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "ux_analysis_services_tenant_name",
                schema: "inspection",
                table: "analysis_services",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_case_claims_service",
                schema: "inspection",
                table: "case_claims",
                column: "AnalysisServiceId");

            migrationBuilder.CreateIndex(
                name: "ix_case_claims_tenant",
                schema: "inspection",
                table: "case_claims",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_case_claims_active_per_case",
                schema: "inspection",
                table: "case_claims",
                column: "CaseId",
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            // ----- RLS: enable + FORCE + tenant_isolation_* per table ---------
            // Same shape as the Sprint 12 R3 migration. The COALESCE
            // fallback to '0' fail-closes when no tenant is set on the
            // session (TenantConnectionInterceptor must run first).
            foreach (var t in TenantedTables)
            {
                migrationBuilder.Sql($"ALTER TABLE \"inspection\".\"{t}\" ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE \"inspection\".\"{t}\" FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY \"tenant_isolation_{t}\" ON \"inspection\".\"{t}\" "
                    + "USING ("
                    + "  \"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                    + ") "
                    + "WITH CHECK ("
                    + "  \"TenantId\" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint "
                    + ");");
            }

            // ----- Un-deletable guard for built-in "All Locations" service -----
            // Defence-in-depth for the Phase B service-layer guard. A
            // BEFORE DELETE trigger raises an exception if any row with
            // IsBuiltInAllLocations = TRUE is targeted.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION inspection.fn_prevent_built_in_all_locations_delete()
RETURNS TRIGGER AS $$
BEGIN
    IF OLD.""IsBuiltInAllLocations"" = TRUE THEN
        RAISE EXCEPTION 'Cannot delete the built-in ""All Locations"" AnalysisService (tenant_id=%, service_id=%)',
            OLD.""TenantId"", OLD.""Id"";
    END IF;
    RETURN OLD;
END;
$$ LANGUAGE plpgsql;");

            migrationBuilder.Sql(@"
CREATE TRIGGER trg_prevent_built_in_all_locations_delete
    BEFORE DELETE ON inspection.analysis_services
    FOR EACH ROW
    EXECUTE FUNCTION inspection.fn_prevent_built_in_all_locations_delete();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop trigger + function FIRST (depend on the table).
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_prevent_built_in_all_locations_delete ON inspection.analysis_services;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS inspection.fn_prevent_built_in_all_locations_delete();");

            // Drop policies (cascades when table is dropped, but explicit
            // for clarity + symmetry with Up()).
            foreach (var t in TenantedTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS \"tenant_isolation_{t}\" ON \"inspection\".\"{t}\";");
            }

            migrationBuilder.DropTable(
                name: "analysis_service_locations",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "analysis_service_users",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "case_claims",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "analysis_services",
                schema: "inspection");
        }
    }
}
