using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    // Mail confirmation (identity hub T4c): password accounts that exist when it ships count as confirmed (grandfathered),
    // so turning mail on never makes an existing player wait. Data only, and only users.email_confirmed_at where it is
    // null; it is set to created_at, which no real confirmation writes, so Down clears exactly these rows again.
    public partial class PasswordAccountsConfirmed : Migration
    {
        public const string UpSql = "UPDATE users SET email_confirmed_at = created_at WHERE password_hash IS NOT NULL AND email IS NOT NULL AND email_confirmed_at IS NULL";
        public const string DownSql = "UPDATE users SET email_confirmed_at = NULL WHERE password_hash IS NOT NULL AND email_confirmed_at = created_at";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownSql);
        }
    }
}
