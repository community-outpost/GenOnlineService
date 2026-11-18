using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class InvestigationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An online build: InnoDB adds the index in place while the table keeps taking writes, or the statement fails
            migrationBuilder.Sql("ALTER TABLE `session_events` ADD INDEX `ix_session_events_country_user` (`country`, `user_id`, `created_at`), ALGORITHM=INPLACE, LOCK=NONE;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE `session_events` DROP INDEX `ix_session_events_country_user`, ALGORITHM=INPLACE, LOCK=NONE;", suppressTransaction: true);
        }
    }
}
