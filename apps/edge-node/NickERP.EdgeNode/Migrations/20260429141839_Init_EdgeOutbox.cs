using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.EdgeNode.Migrations
{
    /// <inheritdoc />
    public partial class Init_EdgeOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "edge_outbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    EventTypeHint = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EdgeTimestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EdgeNodeId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TenantId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReplayedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReplayAttempts = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    LastReplayError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edge_outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_edge_outbox_pending",
                table: "edge_outbox",
                column: "Id",
                filter: "\"ReplayedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edge_outbox");
        }
    }
}
