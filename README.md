# Services
GeneralsOnline Game Services Code provides RESTful web services which act as a replacement and modernization of the GameSpy functionality found in the original release of Command & Conquer: Generals - Zero Hour

# Build Status: Windows
[![Windows (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Windows.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Windows.yml)

# Build Status: Linux
[![Linux (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux-MUSL.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux-MUSL.yml)
[![Linux MUSL (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux.yml)

# Build Status: MacOS
[![MacOS (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/MacOS.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/MacOS.yml)

# Original Game Functionality Implemented
- Quick Match
- Custom Match
- Ladders
- Rooms & Chat
- Profiles
- Player Stats
- Friends / Buddies / Social System
- Global / Daily Stats
- Auto-update

# Required Dependencies
- Microsoft Visual Studio (2026 Community Edition recommended)
- A database server, one of:
  - PostgreSQL (PostgreSQL 15 or higher recommended)
  - MySQL/MariaDB (MariaDB 12.1.2 or higher recommended), for installs that stay on MariaDB
- A database management tool or command line (e.g. pgAdmin, DBeaver, HeidiSQL, psql or mariadb)
- .NET SDK 10

# Optional Dependencies (not required for development, required for live)
- Discord App ID
- S3 compatible storage
- STUN + TURN servers

# How To Build
- Sync the repository
- Open GenServices.sln
- Build the solution for x64, Windows (or your architecture & OS if different)
- Edit appsettings.json and fill out any TODO sections (e.g. token settings, database settings)
- Choose the database in the `Database` section of appsettings.json:
  - `db_provider` is `"postgres"` (PostgreSQL, the default when the key is missing or empty) or `"mysql"` (MySQL/MariaDB, `"mariadb"` is accepted too). Any other value stops the service at startup
  - Create an empty database and a user with full access to it, then fill in `db_host`, `db_port` (5432 for PostgreSQL and 3306 for MySQL/MariaDB when missing), `db_name`, `db_username` and `db_password`
  - PostgreSQL only: `db_host` may list several servers (`"10.66.0.1,10.66.0.2"`); set `db_target_session_attributes` to `"primary"` so the service always connects to the writable one, also after a failover. With `"mysql"` the setting must stay empty, the service stops at startup otherwise
  - New installs should use PostgreSQL
- Create the database schema by running `GenOnlineService migrate` (there is no SQL to import)
- Run GenOnlineService.exe. The service never changes the schema itself and refuses to start while migrations are pending
- When upgrading, stop the service, run `GenOnlineService migrate --yes`, then start the new version. With multiple instances, the old instances may keep running during the migration only if it is backward compatible, then roll the instances

# Releases
- Releases are tags on `main` using semantic versioning. The service is in alpha: versions are `0.x`, and a minor version (`0.2` to `0.3`) may contain breaking changes to the client protocol, the configuration or the schema; patch versions only contain fixes
- A `vX.Y.Z` tag (only plain tags count as versions; any other tag is ignored) builds the linux-x64 artifact (`genonlineservice-<version>-linux-x64.tar.gz` plus its SHA-256, attached to the GitHub release, marked as a pre-release while in `0.x`) and the container image `ghcr.io/<owner>/genonlineservice:<version>` (also tagged with the 8-character commit). `latest` points at the newest release; deployments should still pin an exact version or commit
- The git tag is the only source of the version (the project file has none) and `GET /healthz` reports it: `0.1.0+<8-character commit>` on the tag `v0.1.0`, `0.1.0+20.<commit>` 20 commits after it, `0.0.0+<commit>` when no `v*` tag is reachable
- The build always computes it from git (an MSBuild target running `git describe`, there is no override), so it needs the tags and the `.git` directory: CI checks out with `fetch-depth: 0` (and fails a build whose version does not match the commit or tag) and the Docker build copies `.git` into the build stage; a build without git reports `0.0.0+unknown`. In a git worktree `.git` only points into the main repository, so build the image with `scripts/docker-build.sh`, which passes that repository as the `git` build context; a plain `docker build` there stops with that hint. Sentry's release uses the same string. The build also writes it to `version.txt` in the publish output, which CI reads to name the artifact: `genonlineservice-<X.Y.Z>-linux-x64.tar.gz` for tags, the commit otherwise; image tags cannot contain `+`, so they stay the semver tag, the commit, and `main`

# Running The Image
- The image ships no `appsettings.json` (the sample binds `localhost` with HTTPS, which has no certificate in a container). Mount your own at `/app/appsettings.json` and mount `data/`, `crcfiles/` and `Exceptions/` (`/app/data`, `/app/crcfiles`, `/app/Exceptions`). The container runs as a non-root user, so they must be writable by it, and a mounted `data/` replaces the shipped one: start it from a copy of `GenOnlineService/data`
- Either leave the `Kestrel` section out of that file, so the image listens on `http://0.0.0.0:8080` (`ASPNETCORE_HTTP_PORTS`), or define a single HTTP endpoint on `0.0.0.0:8080`. Terminate TLS in front of the container
- Apply the schema with the same mounts: `docker run --rm -v ./appsettings.json:/app/appsettings.json:ro ghcr.io/<owner>/genonlineservice:<version> migrate --yes`

# Importing The Old Production Database
The schema is v1 (`docs/schema.md`): every environment has its own database, and the old production MariaDB is converted once into the prod environment. `GenOnlineService import [--dry-run] [--yes]` reads the database in the `Import` section (`db_provider` `mariadb` or `mysql`, `db_host`, `db_port`, `db_ssl_mode`, `db_name`, `db_username`, `db_password`) and writes into the database in the `Database` section (PostgreSQL or MySQL/MariaDB). The source is only read, inside one consistent snapshot.
- Create an empty target database and run `GenOnlineService migrate --yes` against it. The import refuses a target with pending migrations or with any rows
- Validate: the import first reads everything and writes nothing. It prints the users that are dropped (`Ghost` and `DevAccount` accounts: their match slots stay with no user), the staff remap report (every user with `admin` or `permissionslevel`, which are not imported: grant roles again with `roles grant` or Keycloak groups) and the findings that block the import: `duplicate_identities`, `duplicate_emails`, `username_collisions`, `invalid_usernames`, `orphaned_rows`, `invalid_values`, `invalid_json`. Resolve them in the source, or waive them in `Import:waive` (a list of those names). A waived duplicate stays with the user who signed in last; a waived username collision or invalid name gets `<name>_<id>`; waived orphans and invalid rows are left out or lose their user
- Convert: one transaction on the target, table by table. Legacy bans become local `play` bans with an `audit_log` entry (`Import:legacy_bans` `local`, the default, or `skip`). Device values are stored as HMAC-SHA-256 with the key at `Devices:hmac_key_path` (`data/device_hmac.key` by default), created when missing: back it up with the database and give the service the same key. Every user is imported as `active`; the old `users.active` column is ignored. Old statistics (`daily_stats`, `service_stats`, `connection_outcomes`) are not imported
- Verify: every target table is read back and compared with what was written (row counts and SHA-256 row checksums), and the identity sequences are checked to continue after the highest imported id. Only then does the transaction commit; `--dry-run` rolls it back instead
- The report is printed and written to `Import:report_path` (`import-report-<time>.txt` by default). Exit code 2 means blocking findings, 1 a failure (nothing was written)
- Cutover: stop the old service, run the import, start the new deploy. Devices whose user last signed in more than 180 days ago are removed by the first retention purge

# Changing The Database Schema
The schema is defined by the EF Core model (`GenOnlineService\Database`) and applied through code-first migrations. Migrations are only applied by `GenOnlineService migrate`, never by the service itself.

The model is shared, but every provider has its own context (`MySqlAppDbContext`, `PostgresAppDbContext`) and its own migrations (`GenOnlineService\Database\Migrations\MySql` and `...\Postgres`). **Every model change needs a migration for both contexts.** The conventions and portability rules are in `docs/schema.md`: no unsigned types, UTC timestamps (`datetime(6)` / `timestamptz`), JSON as `json` / `jsonb`, uniqueness through `*_normalized` columns. Provider specific column types belong in the derived contexts (`Database.Providers.cs`), not in the shared configuration classes. Raw SQL statements need a branch for both providers. Do not write SQL outside generated migrations.
- Restore the local tools once: `dotnet tool restore`
- After changing the model, create the migrations from the `GenOnlineService` folder:
  - `dotnet ef migrations add <Name> --context MySqlAppDbContext -o Database/Migrations/MySql -n GenOnlineService.Migrations.MySql`
  - `dotnet ef migrations add <Name> --context PostgresAppDbContext -o Database/Migrations/Postgres -n GenOnlineService.Migrations.Postgres`
- Keep the `-n` argument, otherwise the generated namespace collides with the `Database` namespace used by the data access code and the build fails
- The first `migrations add` for a context writes its model snapshot to a stray `GenOnlineService\GenOnlineService\Migrations\...` folder. Move that file next to the migration (later migrations update it in place) and delete the stray folder
- Check that nothing is missing with `dotnet ef migrations has-pending-model-changes --context <MySqlAppDbContext|PostgresAppDbContext>`
- Commit the generated migration files together with the model change
- Keep schema changes backward compatible: add new columns/tables first and drop old ones in a later release, so an older instance keeps working while a newer one has already migrated

## The migrate command
- `GenOnlineService migrate` uses the configured `db_provider`, lists the applied and pending migrations and asks for confirmation. It does not start the service. Without `--yes` it refuses to run when input is redirected (e.g. in CI)

# Staff Roles And Bans
Staff roles live in the `user_roles` table and bans in the `bans` table. Every grant, revoke, ban, unban and kick is also written to the append-only `audit_log` table. Rows in both tables carry a `source`: `local` for changes made in this environment, `upstream:<provider>` for rows an upstream sign-in provider manages. Reasons are up to 512 characters. The commands below use the `Database` section, need all migrations applied, and work while the service runs: it applies changes within about 5 seconds without a restart. A role change keeps the player logged in, a ban disconnects them.
- `GenOnlineService roles list [<user_id>]`, `roles grant <user_id> <role> [--reason <text>] [--by <user_id>]`, `roles revoke <user_id> <role> [--reason <text>] [--by <user_id>]`. Roles are `moderator` and `admin`; `admin` has every `moderator` permission and can manage roles
- `GenOnlineService bans list <user_id>`, `bans add <user_id> --reason <text> [--days <n>] [--by <user_id>]` (without `--days` the ban is permanent), `bans revoke <user_id> [--reason <text>] [--by <user_id>]`. Revoked and expired bans stay in the table as history
- `--by` records which staff account acted, otherwise the log shows `cli`
- Chat reports and direct messages: staff read chat only through a report or an audited lookup, and every read is written to `audit_log`. `GenOnlineService reports list` (open reports), `reports show <report_id> --by <user_id>` (the stored snapshot: the reported message, up to 10 before and up to 10 after it; until the retention purge freezes the later messages it also prints them live, marked not yet frozen), `reports close <report_id> [--reason <text>] [--by <user_id>]`, `messages show <message_id> --reason <text> --by <user_id>` (audited lookup of one message, the reason and the staff account are required; `--by` must name an existing user in both `show` commands), `messages purge` (runs the 30 day chat retention purge now, the service also runs it hourly). Moderators and admins hold `chat.delete_any` for `DeleteMessage` in the hub
- `Realtime:history_size` (default 50, at most 100) is the number of channel messages replayed in `ChannelHistory` on join and reconnect
- Schema v1 has no `users.admin` or `users.banned`: the import lists old staff to grant again with `roles grant`, and turns legacy bans into `bans` rows
- The Discord bot uses slash commands only and requests no privileged intent. Set `Discord:discord_guild_id` so it registers them on start. `/players` and `/lobbies` are public; `/kick`, `/whois`, `/uptime`, `/peak` and `/announce` work only in the admin commands channel. They are allowed when the caller's Discord account is linked (a `discord` row in `identities`) to a GenOnline account with the needed permission (`moderation.kick` for kick and whois, `server.stats` and `announcements.send` for the admin commands), or when the Discord user is listed in `Discord:discord_admins`, which is kept until staff accounts are linked

# Sign-In And Sessions
Every environment is its own identity provider (design: `docs/identity.md`): the `Accounts` section runs ASP.NET Core Identity and an OpenIddict server in this process (`Accounts:enabled`), over the `users` and `identities` tables. With `Accounts:enabled` false the service only validates access tokens of the accounts instance named in `Accounts:issuer`.
- Upstream sign-in: each entry in `Accounts:providers` is an OpenID Connect provider (Keycloak in the official deployment) with `issuer`, `client_id`, `client_secret`; its redirect URI is `<service>/users/auth/<name>/callback`. `allow_sign_up` creates an account for an unknown identity, `only_provider` skips the provider choice, `auto_link_verified_email` links by a verified email. A `trusted` provider may also link an existing account through `link_claim` (`go_user_id`), and maps its `groups` claim to roles (`role_groups`) and a play ban (`ban_group`); those rows have `source = upstream:<name>` and are rewritten at each sign-in, `local` rows are never touched
- `group_sync` (trusted providers): every `interval_seconds` the service reads the realm's admin events (`GROUP_MEMBERSHIP`; enable admin events in the realm) with a client that has `view-events` and `view-users`, and applies group changes at once. Removal from a staff group also signs the user out of the game. If Keycloak is unreachable nothing changes and the sync resumes from its last event time (`application_settings`)
- Game clients sign in with the authorization code flow and PKCE (`/oauth/authorize`, `/oauth/token`, RFC 8252 loopback redirect on any port, scopes `openid offline_access game`), then register the game session with `POST /api/v1/auth/session` (device ids) before opening the realtime connection; a game client that skips it gets `409 {"code":"session.not_registered"}`. Logout is `DELETE /api/v1/auth/session` plus `POST /oauth/revoke`. Clients are listed in `Accounts:clients`
- Tokens: access tokens are RS256 JWTs for 15 minutes (`access_token_minutes`), refresh tokens are stored, rotate on every use and live 30 days (`refresh_token_days`); presenting a used refresh token after `refresh_reuse_leeway_seconds` revokes the whole sign-in. Signing and encryption keys are PFX files in `signing_certificate_paths` and `encryption_certificate_paths` (list a new key next to the old one to rotate); `ephemeral_keys` is for development only
- Bans and blocks: a play ban keeps sign-in working (so the player can read the reason and appeal), refuses the game API with `403 {"error":"moderation.banned"}`, closes realtime and revokes the game clients' sign-ins. `users.state = blocked` refuses sign-in and every API call, and revokes every sign-in. Both apply within about 5 seconds
- `POST /api/v1/users/:id/revoke_sessions` signs a user out everywhere; it needs a `gopat-` token with the privileged `users.revoke` permission
- HTTP rate limiting belongs to the proxy, not the service: flood protection, per-path limits (`/oauth/token`, `/oauth/authorize`, the sign-in pages, the API) and per-IP blocking belong to the reverse proxy in front of it (Envoy with CrowdSec in the official deployment); a self-hoster without such a proxy should put one in front. Set `TrustedProxies` to the proxy addresses only, so forwarded headers are trusted from the proxy alone and logs and `allowed_ips` see the real client address. Per-account limits (failed sign-ins lead to lockout) come with local passwords and TOTP in phase 3

# API Tokens
Machines (website backend, Discord bot, partners, monitoring) call the API with `Authorization: Bearer gopat-<40 characters>`, the only machine credential (design: `docs/api-tokens.md`). A token belongs to a service account or to a staff user, carries explicit scopes and always expires. Each request gets the token scopes the owner still holds (service account grants, or the user's staff roles), so removing a grant, a role or a token narrows access within about 5 seconds. Every refused token gets the same `401` body; the reason is logged with the token id and counted in `genonline.api_token.auth_failures`. Only the SHA-256 of a token is stored, the token itself is printed once.
- `GenOnlineService service-accounts list`, `service-accounts create <name> [--description <text>]`, `service-accounts grant <name> <permission>`, `service-accounts revoke-grant <name> <permission>`, `service-accounts disable <name>`, `service-accounts enable <name>`
- `GenOnlineService tokens list [--owner service:<name>|user:<id>] [--all true]`, `tokens create --owner service:<name>|user:<id> --name <name> --scopes <p1,p2> --expires <yyyy-mm-dd> [--allowed-ips <cidr,cidr>]`, `tokens rotate <id> [--overlap-minutes <0-1440>] [--expires <yyyy-mm-dd>]`, `tokens revoke <id> [--reason <text>]`
- Machine permissions (`users.revoke`, `ops.database`, `match_history.read`, `lobbies.read`) are held only by service accounts. The privileged ones (`users.revoke`, `ops.database`) can only be granted and minted here on the host, and their tokens need `--allowed-ips` and live at most `ApiTokens:privileged_max_lifetime_days` (30). Other tokens live at most `ApiTokens:max_lifetime_days` (365); an owner has at most `ApiTokens:max_active_per_owner` (20) active tokens
- Rotation stops the old token at once unless `--overlap-minutes` keeps it working for a planned switch. Every mint, rotate, revoke and grant is written to `audit_log`, never the token
- `genonline.api_token.expires_in{token_id,name,owner}` reports the seconds left per active token (alert at 14 and 3 days); the service also logs a daily warning for tokens inside `ApiTokens:expiry_warning_days`. Revoked and expired rows are deleted after `ApiTokens:retention_days` (90)
- `allowed_ips` needs the real client address: set `TrustedProxies` when the service runs behind a proxy

# Observability

The service can export OpenTelemetry logs, metrics, and traces over OTLP, with Sentry as an optional destination for errors, logs, and traces. See [docs/observability.md](docs/observability.md) for configuration, coverage, metric names, privacy controls, and an Alloy example.
