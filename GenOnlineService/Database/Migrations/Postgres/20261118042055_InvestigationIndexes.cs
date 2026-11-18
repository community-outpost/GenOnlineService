using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InvestigationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CONCURRENTLY builds the index without blocking writes on a live table; it cannot run inside a transaction, so a
            // failed run can leave an invalid index behind: it is dropped first, so running the migration again repairs it
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"ix_session_events_country_user\";", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY \"ix_session_events_country_user\" ON \"session_events\" (\"country\", \"user_id\", \"created_at\");", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"ix_session_events_country_user\";", suppressTransaction: true);
        }
    }
}
