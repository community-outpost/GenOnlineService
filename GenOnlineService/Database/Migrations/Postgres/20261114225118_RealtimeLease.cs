using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
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
                    id = table.Column<int>(type: "integer", nullable: false),
                    holder = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    standby = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    standby_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    handover_requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realtime_lease", x => x.id);
                });

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
