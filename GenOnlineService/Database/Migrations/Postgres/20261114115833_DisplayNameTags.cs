using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class DisplayNameTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_users_username_normalized",
                table: "users");

            // Names were unique before tags, so tag 1 keeps every existing name#tag unique
            migrationBuilder.AddColumn<int>(
                name: "name_tag",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "name_tag",
                table: "user_username_history",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_username_normalized_name_tag",
                table: "users",
                columns: new[] { "username_normalized", "name_tag" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_users_username_normalized_name_tag",
                table: "users");

            migrationBuilder.DropColumn(
                name: "name_tag",
                table: "users");

            migrationBuilder.DropColumn(
                name: "name_tag",
                table: "user_username_history");

            migrationBuilder.CreateIndex(
                name: "ux_users_username_normalized",
                table: "users",
                column: "username_normalized",
                unique: true);
        }
    }
}
