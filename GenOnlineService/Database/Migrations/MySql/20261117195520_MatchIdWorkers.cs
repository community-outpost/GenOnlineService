using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class MatchIdWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "match_id_workers",
                columns: table => new
                {
                    worker_id = table.Column<int>(type: "int", nullable: false),
                    holder = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    reserved_until = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_id_workers", x => x.worker_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "match_id_workers",
                columns: new[] { "worker_id", "generation", "holder", "reserved_until" },
                values: new object[,]
                {
                    { 0, 0L, "", 0L },
                    { 1, 0L, "", 0L },
                    { 2, 0L, "", 0L },
                    { 3, 0L, "", 0L },
                    { 4, 0L, "", 0L },
                    { 5, 0L, "", 0L },
                    { 6, 0L, "", 0L },
                    { 7, 0L, "", 0L },
                    { 8, 0L, "", 0L },
                    { 9, 0L, "", 0L },
                    { 10, 0L, "", 0L },
                    { 11, 0L, "", 0L },
                    { 12, 0L, "", 0L },
                    { 13, 0L, "", 0L },
                    { 14, 0L, "", 0L },
                    { 15, 0L, "", 0L },
                    { 16, 0L, "", 0L },
                    { 17, 0L, "", 0L },
                    { 18, 0L, "", 0L },
                    { 19, 0L, "", 0L },
                    { 20, 0L, "", 0L },
                    { 21, 0L, "", 0L },
                    { 22, 0L, "", 0L },
                    { 23, 0L, "", 0L },
                    { 24, 0L, "", 0L },
                    { 25, 0L, "", 0L },
                    { 26, 0L, "", 0L },
                    { 27, 0L, "", 0L },
                    { 28, 0L, "", 0L },
                    { 29, 0L, "", 0L },
                    { 30, 0L, "", 0L },
                    { 31, 0L, "", 0L }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "match_id_workers");
        }
    }
}
