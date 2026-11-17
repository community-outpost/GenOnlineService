using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class StaffListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CONCURRENTLY builds each index without blocking writes on a live table; it cannot run inside a transaction, so a
            // failed run can leave an index behind, or an invalid one: each is dropped first, so running the migration again repairs it
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"ix_users_created_at\";", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY \"ix_users_created_at\" ON \"users\" (\"created_at\", \"id\");", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"ix_users_last_sign_in_at\";", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY \"ix_users_last_sign_in_at\" ON \"users\" (\"last_sign_in_at\" DESC, \"id\" DESC);", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"ix_matches_started_at\";", suppressTransaction: true);
            migrationBuilder.Sql("CREATE INDEX CONCURRENTLY \"ix_matches_started_at\" ON \"matches\" (\"started_at\");", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"ix_users_created_at\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"ix_users_last_sign_in_at\";", suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS \"ix_matches_started_at\";", suppressTransaction: true);
        }
    }
}
