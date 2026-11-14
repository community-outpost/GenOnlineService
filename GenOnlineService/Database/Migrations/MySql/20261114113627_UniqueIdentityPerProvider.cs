using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class UniqueIdentityPerProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_identities_user_id_provider",
                table: "identities",
                columns: new[] { "user_id", "provider" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "ix_identities_user_id",
                table: "identities");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_identities_user_id",
                table: "identities",
                column: "user_id");

            migrationBuilder.DropIndex(
                name: "ux_identities_user_id_provider",
                table: "identities");
        }
    }
}
