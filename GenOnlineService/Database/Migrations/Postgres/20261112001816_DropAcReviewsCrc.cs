using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class DropAcReviewsCrc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ac_reviews_crc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ac_reviews_crc",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    original_crc = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    diff_crc = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    most_recent_match = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ac_reviews_crc", x => new { x.user_id, x.original_crc, x.diff_crc, x.most_recent_match });
                });
        }
    }
}
