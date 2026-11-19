using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class MatchIdsWithoutIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MySQL refuses to change a column that a foreign key references, so both keys on matches.id are dropped around the change
            migrationBuilder.DropForeignKey(
                name: "FK_match_players_matches_match_id",
                table: "match_players");

            migrationBuilder.DropForeignKey(
                name: "FK_match_publications_matches_match_id",
                table: "match_publications");

            migrationBuilder.AlterColumn<long>(
                name: "id",
                table: "matches",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .OldAnnotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn);

            migrationBuilder.AddForeignKey(
                name: "FK_match_players_matches_match_id",
                table: "match_players",
                column: "match_id",
                principalTable: "matches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_match_publications_matches_match_id",
                table: "match_publications",
                column: "match_id",
                principalTable: "matches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // MySQL refuses to change a column that a foreign key references, so both keys on matches.id are dropped around the change
            migrationBuilder.DropForeignKey(
                name: "FK_match_players_matches_match_id",
                table: "match_players");

            migrationBuilder.DropForeignKey(
                name: "FK_match_publications_matches_match_id",
                table: "match_publications");

            migrationBuilder.AlterColumn<long>(
                name: "id",
                table: "matches",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint")
                .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn);

            migrationBuilder.AddForeignKey(
                name: "FK_match_players_matches_match_id",
                table: "match_players",
                column: "match_id",
                principalTable: "matches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_match_publications_matches_match_id",
                table: "match_publications",
                column: "match_id",
                principalTable: "matches",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
