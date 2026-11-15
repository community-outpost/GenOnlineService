using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class Chat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    channel_id = table.Column<long>(type: "bigint", nullable: true),
                    lobby_id = table.Column<long>(type: "bigint", nullable: true),
                    match_id = table.Column<long>(type: "bigint", nullable: true),
                    sender_id = table.Column<long>(type: "bigint", nullable: false),
                    recipient_id = table.Column<long>(type: "bigint", nullable: true),
                    type = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    content = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_messages", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "chat_read_markers",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    conversation = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    last_read_message_id = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_read_markers", x => new { x.user_id, x.conversation });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "chat_reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    reporter_id = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    snapshot = table.Column<string>(type: "json", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    closed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_reports", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_channel",
                table: "chat_messages",
                columns: new[] { "kind", "channel_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_created_at",
                table: "chat_messages",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_lobby",
                table: "chat_messages",
                columns: new[] { "lobby_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_recipient_sender",
                table: "chat_messages",
                columns: new[] { "recipient_id", "sender_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_sender_recipient",
                table: "chat_messages",
                columns: new[] { "sender_id", "recipient_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_read_markers_updated_at",
                table: "chat_read_markers",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "ix_chat_reports_reporter_id",
                table: "chat_reports",
                column: "reporter_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_reports_status_closed_at",
                table: "chat_reports",
                columns: new[] { "status", "closed_at" });

            migrationBuilder.CreateIndex(
                name: "ux_chat_reports_message_reporter",
                table: "chat_reports",
                columns: new[] { "message_id", "reporter_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "chat_read_markers");

            migrationBuilder.DropTable(
                name: "chat_reports");
        }
    }
}
