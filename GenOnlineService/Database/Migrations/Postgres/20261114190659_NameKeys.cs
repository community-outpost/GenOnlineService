using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class NameKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name_key",
                table: "users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            // SQL cannot compute the plugin key, so each row holds a unique placeholder (NameKeyBackfill.Placeholder) and
            // "GenOnlineService migrate" computes the keys right after, reporting look-alike names instead of merging them
            migrationBuilder.Sql(
                "UPDATE users SET name_key = chr(1) || id::text;");

            migrationBuilder.CreateIndex(
                name: "ux_users_name_key_name_tag",
                table: "users",
                columns: new[] { "name_key", "name_tag" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_users_name_key_name_tag",
                table: "users");

            migrationBuilder.DropColumn(
                name: "name_key",
                table: "users");
        }
    }
}
