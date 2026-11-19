using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class IdentifierHashesUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_identifier_hashes_kind_value",
                table: "identifier_hashes");

            migrationBuilder.CreateIndex(
                name: "ux_identifier_hashes_kind_value",
                table: "identifier_hashes",
                columns: new[] { "kind", "value_hmac" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_identifier_hashes_kind_value",
                table: "identifier_hashes");

            migrationBuilder.CreateIndex(
                name: "ix_identifier_hashes_kind_value",
                table: "identifier_hashes",
                columns: new[] { "kind", "value_hmac" });
        }
    }
}
