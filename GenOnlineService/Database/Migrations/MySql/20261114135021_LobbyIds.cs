using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class LobbyIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lobby_ids",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false),
                    next_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lobby_ids", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            // the counter starts above every lobby id already stored, so an upgraded database never reuses one
            migrationBuilder.Sql(
                "INSERT INTO lobby_ids (id, next_id) SELECT 1, GREATEST(COALESCE((SELECT MAX(lobby_id) FROM chat_messages), 0), COALESCE((SELECT MAX(lobby_id) FROM connection_reports), 0)) + 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lobby_ids");
        }
    }
}
