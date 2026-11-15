using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class RealtimeLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "realtime_lease",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false),
                    holder = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    standby = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    standby_expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    handover_requested_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realtime_lease", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "realtime_lease",
                columns: new[] { "id", "epoch", "expires_at", "handover_requested_at", "holder", "standby", "standby_expires_at" },
                values: new object[] { 1, 0L, new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, "", null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "realtime_lease");
        }
    }
}
