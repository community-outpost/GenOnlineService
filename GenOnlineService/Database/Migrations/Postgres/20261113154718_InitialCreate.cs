using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "anticheat_module_reports",
                columns: table => new
                {
                    report_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    match_id = table.Column<long>(type: "bigint", nullable: false),
                    module_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    module_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    module_size = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_anticheat_module_reports", x => x.report_id);
                });

            migrationBuilder.CreateTable(
                name: "anticheat_new_account_games",
                columns: table => new
                {
                    report_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    match_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_anticheat_new_account_games", x => x.report_id);
                });

            migrationBuilder.CreateTable(
                name: "anticheat_probe_reports",
                columns: table => new
                {
                    report_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    match_id = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_anticheat_probe_reports", x => x.report_id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_id = table.Column<long>(type: "bigint", nullable: true),
                    actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bans",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    restriction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "blocks",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    blocked_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blocks", x => new { x.user_id, x.blocked_user_id });
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value_hmac = table.Column<byte[]>(type: "bytea", fixedLength: true, maxLength: 32, nullable: false),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "friend_requests",
                columns: table => new
                {
                    from_user_id = table.Column<long>(type: "bigint", nullable: false),
                    to_user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friend_requests", x => new { x.from_user_id, x.to_user_id });
                });

            migrationBuilder.CreateTable(
                name: "friendships",
                columns: table => new
                {
                    user_id_low = table.Column<long>(type: "bigint", nullable: false),
                    user_id_high = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friendships", x => new { x.user_id_low, x.user_id_high });
                });

            migrationBuilder.CreateTable(
                name: "identities",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    extern_uid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    extern_username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "leaderboard_entries",
                columns: table => new
                {
                    period = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    wins = table.Column<int>(type: "integer", nullable: false),
                    losses = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leaderboard_entries", x => new { x.period, x.period_start, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "matches",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_user_id = table.Column<long>(type: "bigint", nullable: true),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lobby_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    roster_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    map_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    map_path = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_official_map = table.Column<bool>(type: "boolean", nullable: false),
                    vanilla_teams = table.Column<bool>(type: "boolean", nullable: false),
                    starting_cash = table.Column<long>(type: "bigint", nullable: false),
                    limit_superweapons = table.Column<bool>(type: "boolean", nullable: false),
                    track_stats = table.Column<bool>(type: "boolean", nullable: false),
                    allow_observers = table.Column<bool>(type: "boolean", nullable: false),
                    max_cam_height = table.Column<int>(type: "integer", nullable: false),
                    exe_crc = table.Column<long>(type: "bigint", nullable: false),
                    ini_crc = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "player_network",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    relay_region = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    port_mapping = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ipv4 = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    ipv6 = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_network", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "player_preferences",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    color = table.Column<int>(type: "integer", nullable: true),
                    side = table.Column<int>(type: "integer", nullable: true),
                    map = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    starting_money = table.Column<int>(type: "integer", nullable: true),
                    limit_superweapons = table.Column<bool>(type: "boolean", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_preferences", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "player_ratings",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    matches_played = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_ratings", x => new { x.user_id, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "player_stats",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    stats = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_stats", x => x.user_id);
                });

            migrationBuilder.CreateTable(
                name: "session_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    client = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    client_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    exe_crc = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    granted_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.user_id, x.role, x.source });
                });

            migrationBuilder.CreateTable(
                name: "user_token_states",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    session_type = table.Column<int>(type: "integer", nullable: false),
                    token_generation = table.Column<int>(type: "integer", nullable: false),
                    refresh_jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    previous_refresh_jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    previous_refresh_jti_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_token_states", x => new { x.user_id, x.session_type });
                });

            migrationBuilder.CreateTable(
                name: "user_username_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_username_history", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    username_normalized = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    email_normalized = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    email_confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    password_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failed_sign_in_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_sign_in_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    renamed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "match_players",
                columns: table => new
                {
                    match_id = table.Column<long>(type: "bigint", nullable: false),
                    slot = table.Column<short>(type: "smallint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: true),
                    team = table.Column<short>(type: "smallint", nullable: true),
                    side = table.Column<int>(type: "integer", nullable: true),
                    color = table.Column<int>(type: "integer", nullable: true),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_players", x => new { x.match_id, x.slot });
                    table.ForeignKey(
                        name: "FK_match_players_matches_match_id",
                        column: x => x.match_id,
                        principalTable: "matches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "match_publications",
                columns: table => new
                {
                    match_id = table.Column<long>(type: "bigint", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_publications", x => x.match_id);
                    table.ForeignKey(
                        name: "FK_match_publications_matches_match_id",
                        column: x => x.match_id,
                        principalTable: "matches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_anticheat_module_reports_user_match_module",
                table: "anticheat_module_reports",
                columns: new[] { "user_id", "match_id", "module_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_target",
                table: "audit_log",
                columns: new[] { "target_kind", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bans_user_id",
                table: "bans",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_devices_kind_value",
                table: "devices",
                columns: new[] { "kind", "value_hmac" });

            migrationBuilder.CreateIndex(
                name: "ix_devices_last_seen_at",
                table: "devices",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ux_devices_user_kind_value",
                table: "devices",
                columns: new[] { "user_id", "kind", "value_hmac" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_friend_requests_to_user_id",
                table: "friend_requests",
                column: "to_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_friendships_user_id_high",
                table: "friendships",
                column: "user_id_high");

            migrationBuilder.CreateIndex(
                name: "ix_identities_user_id",
                table: "identities",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_identities_provider_extern_uid",
                table: "identities",
                columns: new[] { "provider", "extern_uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_leaderboard_entries_user_id",
                table: "leaderboard_entries",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_match_players_user_id",
                table: "match_players",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_match_publications_pending",
                table: "match_publications",
                columns: new[] { "published_at", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_matches_finished_at",
                table: "matches",
                columns: new[] { "finished_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_session_events_created_at",
                table: "session_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_session_events_user_created",
                table: "session_events",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_user_username_history_user_id",
                table: "user_username_history",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_users_email_normalized",
                table: "users",
                column: "email_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_username_normalized",
                table: "users",
                column: "username_normalized",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "anticheat_module_reports");

            migrationBuilder.DropTable(
                name: "anticheat_new_account_games");

            migrationBuilder.DropTable(
                name: "anticheat_probe_reports");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "bans");

            migrationBuilder.DropTable(
                name: "blocks");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "friend_requests");

            migrationBuilder.DropTable(
                name: "friendships");

            migrationBuilder.DropTable(
                name: "identities");

            migrationBuilder.DropTable(
                name: "leaderboard_entries");

            migrationBuilder.DropTable(
                name: "match_players");

            migrationBuilder.DropTable(
                name: "match_publications");

            migrationBuilder.DropTable(
                name: "player_network");

            migrationBuilder.DropTable(
                name: "player_preferences");

            migrationBuilder.DropTable(
                name: "player_ratings");

            migrationBuilder.DropTable(
                name: "player_stats");

            migrationBuilder.DropTable(
                name: "session_events");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "user_token_states");

            migrationBuilder.DropTable(
                name: "user_username_history");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "matches");
        }
    }
}
