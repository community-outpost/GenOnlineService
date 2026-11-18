using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class StaffReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at",
                table: "anticheat_probe_reports",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "reviewed_by",
                table: "anticheat_probe_reports",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at",
                table: "anticheat_new_account_games",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "reviewed_by",
                table: "anticheat_new_account_games",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at",
                table: "anticheat_module_reports",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "reviewed_by",
                table: "anticheat_module_reports",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_events_ip_user",
                table: "session_events",
                columns: new[] { "ip", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_anticheat_probe_reports_reviewed_at",
                table: "anticheat_probe_reports",
                column: "reviewed_at");

            migrationBuilder.CreateIndex(
                name: "ix_anticheat_new_account_games_reviewed_at",
                table: "anticheat_new_account_games",
                column: "reviewed_at");

            migrationBuilder.CreateIndex(
                name: "ix_anticheat_module_reports_reviewed_at",
                table: "anticheat_module_reports",
                column: "reviewed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_session_events_ip_user",
                table: "session_events");

            migrationBuilder.DropIndex(
                name: "ix_anticheat_probe_reports_reviewed_at",
                table: "anticheat_probe_reports");

            migrationBuilder.DropIndex(
                name: "ix_anticheat_new_account_games_reviewed_at",
                table: "anticheat_new_account_games");

            migrationBuilder.DropIndex(
                name: "ix_anticheat_module_reports_reviewed_at",
                table: "anticheat_module_reports");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "anticheat_probe_reports");

            migrationBuilder.DropColumn(
                name: "reviewed_by",
                table: "anticheat_probe_reports");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "anticheat_new_account_games");

            migrationBuilder.DropColumn(
                name: "reviewed_by",
                table: "anticheat_new_account_games");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "anticheat_module_reports");

            migrationBuilder.DropColumn(
                name: "reviewed_by",
                table: "anticheat_module_reports");
        }
    }
}
