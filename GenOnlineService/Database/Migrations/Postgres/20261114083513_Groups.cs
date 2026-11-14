using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Groups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "group_id",
                table: "chat_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat_group_members",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_group_members", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_groups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    owner_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_groups", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_group",
                table: "chat_messages",
                columns: new[] { "group_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_group_members_user_id",
                table: "chat_group_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_chat_group_members_group_user",
                table: "chat_group_members",
                columns: new[] { "group_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_group_members");

            migrationBuilder.DropTable(
                name: "chat_groups");

            migrationBuilder.DropIndex(
                name: "ix_chat_messages_group",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "chat_messages");
        }
    }
}
