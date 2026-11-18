using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class StaffListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An online build: InnoDB adds each index in place while the table keeps taking writes, or the statement fails
            migrationBuilder.Sql("ALTER TABLE `users` ADD INDEX `ix_users_created_at` (`created_at`, `id`), ALGORITHM=INPLACE, LOCK=NONE;", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE `users` ADD INDEX `ix_users_last_sign_in_at` (`last_sign_in_at` DESC, `id` DESC), ALGORITHM=INPLACE, LOCK=NONE;", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE `matches` ADD INDEX `ix_matches_started_at` (`started_at`), ALGORITHM=INPLACE, LOCK=NONE;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE `users` DROP INDEX `ix_users_created_at`, ALGORITHM=INPLACE, LOCK=NONE;", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE `users` DROP INDEX `ix_users_last_sign_in_at`, ALGORITHM=INPLACE, LOCK=NONE;", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE `matches` DROP INDEX `ix_matches_started_at`, ALGORITHM=INPLACE, LOCK=NONE;", suppressTransaction: true);
        }
    }
}
