using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    [global::Database.ImportSafe("Only adds nullable columns, a table and indexes, and widens match_history.map_path")]
    public partial class UpgradeLegacySchema : Migration
    {
        // These items did not exist in databases created from the old structure.sql, but a database that is shared with
        // the website can already have some of them. Every step is skipped when its item is already there (or can be
        // repeated), so the migration is safe to run either way: MySQL DDL is not transactional, so a half applied run
        // has to be repeatable.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            AddColumnIfMissing(migrationBuilder, "users", "google_email", "varchar(255) CHARACTER SET utf8mb4 NULL");
            AddColumnIfMissing(migrationBuilder, "users", "google_id", "varchar(50) CHARACTER SET utf8mb4 NULL");
            AddColumnIfMissing(migrationBuilder, "users", "permissionslevel", "int(11) NULL DEFAULT 0");

            migrationBuilder.Sql(@"CREATE TABLE IF NOT EXISTS `web_sessions` (
    `session_id` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `user_id` bigint NOT NULL,
    `created` timestamp NOT NULL,
    `expiration` timestamp NOT NULL,
    `last_used` timestamp NOT NULL,
    `ip_addr` varchar(45) CHARACTER SET utf8mb4 NOT NULL,
    `os` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `browser` varchar(256) CHARACTER SET utf8mb4 NOT NULL,
    CONSTRAINT `PK_web_sessions` PRIMARY KEY (`session_id`)
) CHARACTER SET=utf8mb4;");

            // The old structure.sql had varchar(128), the service clamps the stored map path to the model's 256
            migrationBuilder.Sql("ALTER TABLE `match_history` MODIFY `map_path` varchar(256) CHARACTER SET utf8mb4 NULL;");

            CreateIndexIfMissing(migrationBuilder, "users", "ix_users_banned", "`banned`");
            CreateIndexIfMissing(migrationBuilder, "user_devices", "idx_user_devices_hwid_0_user", "`hwid_0`, `user_id`");
            CreateIndexIfMissing(migrationBuilder, "user_devices", "idx_user_devices_hwid_1_user", "`hwid_1`, `user_id`");
            CreateIndexIfMissing(migrationBuilder, "user_devices", "idx_user_devices_hwid_2_user", "`hwid_2`, `user_id`");
            CreateIndexIfMissing(migrationBuilder, "user_devices", "idx_user_devices_hwid_triplet_user", "`hwid_0`, `hwid_1`, `hwid_2`, `user_id`");
            CreateIndexIfMissing(migrationBuilder, "user_devices", "idx_user_devices_ip_user", "`ip_addr`, `user_id`");
            CreateIndexIfMissing(migrationBuilder, "match_history", "idx_finished_time", "`finished`, `time_finished`, `match_id`");
            CreateIndexIfMissing(migrationBuilder, "friends_requests", "ix_friends_requests_target_user_id", "`target_user_id`");
            CreateIndexIfMissing(migrationBuilder, "friends", "ix_friends_user_id_2", "`user_id_2`");
            CreateIndexIfMissing(migrationBuilder, "web_sessions", "user_id", "`user_id`");
        }

        private static void AddColumnIfMissing(MigrationBuilder migrationBuilder, string table, string column, string definition)
        {
            RunIfMissing(
                migrationBuilder,
                $"SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = '{table}' AND column_name = '{column}'",
                $"ALTER TABLE `{table}` ADD `{column}` {definition}");
        }

        private static void CreateIndexIfMissing(MigrationBuilder migrationBuilder, string table, string index, string columns)
        {
            RunIfMissing(
                migrationBuilder,
                $"SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND index_name = '{index}'",
                $"CREATE INDEX `{index}` ON `{table}` ({columns})");
        }

        // Plain MySQL has no "ADD COLUMN IF NOT EXISTS" / "CREATE INDEX IF NOT EXISTS", so the statement is prepared on demand
        private static void RunIfMissing(MigrationBuilder migrationBuilder, string existsQuery, string statement)
        {
            migrationBuilder.Sql(
                $"SET @go_sql = IF(EXISTS({existsQuery}), 'DO 0', '{statement.Replace("'", "''")}'); PREPARE go_stmt FROM @go_sql; EXECUTE go_stmt; DEALLOCATE PREPARE go_stmt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "web_sessions");

            migrationBuilder.DropIndex(
                name: "ix_users_banned",
                table: "users");

            migrationBuilder.DropIndex(
                name: "idx_user_devices_hwid_0_user",
                table: "user_devices");

            migrationBuilder.DropIndex(
                name: "idx_user_devices_hwid_1_user",
                table: "user_devices");

            migrationBuilder.DropIndex(
                name: "idx_user_devices_hwid_2_user",
                table: "user_devices");

            migrationBuilder.DropIndex(
                name: "idx_user_devices_hwid_triplet_user",
                table: "user_devices");

            migrationBuilder.DropIndex(
                name: "idx_user_devices_ip_user",
                table: "user_devices");

            migrationBuilder.DropIndex(
                name: "idx_finished_time",
                table: "match_history");

            migrationBuilder.DropIndex(
                name: "ix_friends_requests_target_user_id",
                table: "friends_requests");

            migrationBuilder.DropIndex(
                name: "ix_friends_user_id_2",
                table: "friends");

            migrationBuilder.DropColumn(
                name: "google_email",
                table: "users");

            migrationBuilder.DropColumn(
                name: "google_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "permissionslevel",
                table: "users");
        }
    }
}
