using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial_AddInspectionSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inspection");

            migrationBuilder.CreateTable(
                name: "external_system_instances",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_system_instances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "locations",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "external_system_bindings",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalSystemInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "primary"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_system_bindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_external_system_bindings_external_system_instances_External~",
                        column: x => x.ExternalSystemInstanceId,
                        principalSchema: "inspection",
                        principalTable: "external_system_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_external_system_bindings_locations_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "inspection",
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stations",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_stations_locations_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "inspection",
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scanner_device_instances",
                schema: "inspection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    StationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TypeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scanner_device_instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scanner_device_instances_locations_LocationId",
                        column: x => x.LocationId,
                        principalSchema: "inspection",
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scanner_device_instances_stations_StationId",
                        column: x => x.StationId,
                        principalSchema: "inspection",
                        principalTable: "stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_system_bindings_ExternalSystemInstanceId",
                schema: "inspection",
                table: "external_system_bindings",
                column: "ExternalSystemInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_external_system_bindings_LocationId",
                schema: "inspection",
                table: "external_system_bindings",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "ux_external_bindings_tenant_inst_loc",
                schema: "inspection",
                table: "external_system_bindings",
                columns: new[] { "TenantId", "ExternalSystemInstanceId", "LocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_external_systems_tenant",
                schema: "inspection",
                table: "external_system_instances",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_external_systems_type",
                schema: "inspection",
                table: "external_system_instances",
                column: "TypeCode");

            migrationBuilder.CreateIndex(
                name: "ix_locations_tenant",
                schema: "inspection",
                table: "locations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_locations_tenant_code",
                schema: "inspection",
                table: "locations",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scanner_device_instances_LocationId",
                schema: "inspection",
                table: "scanner_device_instances",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_scanner_device_instances_StationId",
                schema: "inspection",
                table: "scanner_device_instances",
                column: "StationId");

            migrationBuilder.CreateIndex(
                name: "ix_scanners_tenant",
                schema: "inspection",
                table: "scanner_device_instances",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_scanners_tenant_loc",
                schema: "inspection",
                table: "scanner_device_instances",
                columns: new[] { "TenantId", "LocationId" });

            migrationBuilder.CreateIndex(
                name: "ix_scanners_type",
                schema: "inspection",
                table: "scanner_device_instances",
                column: "TypeCode");

            migrationBuilder.CreateIndex(
                name: "IX_stations_LocationId",
                schema: "inspection",
                table: "stations",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "ix_stations_tenant",
                schema: "inspection",
                table: "stations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_stations_tenant_loc_code",
                schema: "inspection",
                table: "stations",
                columns: new[] { "TenantId", "LocationId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_system_bindings",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "scanner_device_instances",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "external_system_instances",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "stations",
                schema: "inspection");

            migrationBuilder.DropTable(
                name: "locations",
                schema: "inspection");
        }
    }
}
