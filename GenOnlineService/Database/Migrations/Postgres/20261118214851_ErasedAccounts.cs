using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ErasedAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "erased_accounts",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    erased_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_erased_accounts", x => x.user_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erased_accounts");
        }
    }
}
