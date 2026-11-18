using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class UserAgreements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_agreements",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    document = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    version = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    agreed_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_agreements", x => new { x.user_id, x.document, x.version });
                    table.ForeignKey(
                        name: "FK_user_agreements_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_agreements");
        }
    }
}
