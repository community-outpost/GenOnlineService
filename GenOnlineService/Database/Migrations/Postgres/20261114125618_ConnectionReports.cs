using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ConnectionReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reporter_id = table.Column<long>(type: "bigint", nullable: false),
                    lobby_id = table.Column<long>(type: "bigint", nullable: false),
                    target_user_id = table.Column<long>(type: "bigint", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    transport = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ip_version = table.Column<int>(type: "integer", nullable: false),
                    events = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_reports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_connection_reports_lobby_id",
                table: "connection_reports",
                column: "lobby_id");

            migrationBuilder.CreateIndex(
                name: "ix_connection_reports_target_updated_at",
                table: "connection_reports",
                columns: new[] { "target_user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_reports_updated_at",
                table: "connection_reports",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "ux_connection_reports_reporter_lobby_target_attempt",
                table: "connection_reports",
                columns: new[] { "reporter_id", "lobby_id", "target_user_id", "attempt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connection_reports");
        }
    }
}
