using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class HandoverTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "handover_confirmed_at",
                table: "realtime_lease",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "handover_target",
                table: "realtime_lease",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "realtime_lease",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "handover_confirmed_at", "handover_target" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "handover_confirmed_at",
                table: "realtime_lease");

            migrationBuilder.DropColumn(
                name: "handover_target",
                table: "realtime_lease");
        }
    }
}
