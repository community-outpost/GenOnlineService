/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using GenOnlineService;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Database
{
	// An error with a message meant for the operator, printed without a stack trace
	public sealed class ImportException : Exception
	{
		public ImportException(string message) : base(message)
		{
		}
	}

	// The "Import" config section: the old production database and the conversion switches
	public sealed record ImportSettings(
		string Host,
		ushort Port,
		string Name,
		string Username,
		string Password,
		MySqlSslMode SslMode,
		bool LegacyBansAsLocal,
		IReadOnlySet<string> Waivers,
		string ReportPath,
		string? KeycloakFile = null);

	public sealed class ImportOptions
	{
		public bool DryRun { get; init; }

		// (prompt, hint printed when input is not interactive) -> proceed
		public Func<string, string, bool> Confirm { get; init; } = (_, _) => false;
	}

	// Validation findings that stop the import until they are resolved in the source or waived in Import:waive
	public static class ImportFindings
	{
		public const string DuplicateIdentities = "duplicate_identities";
		public const string DuplicateEmails = "duplicate_emails";
		public const string UsernameCollisions = "username_collisions";
		public const string InvalidUsernames = "invalid_usernames";
		public const string OrphanedRows = "orphaned_rows";
		public const string InvalidValues = "invalid_values";
		public const string InvalidJson = "invalid_json";

		public static readonly IReadOnlyList<string> All = new[] { DuplicateIdentities, DuplicateEmails, UsernameCollisions, InvalidUsernames, OrphanedRows, InvalidValues, InvalidJson };
	}

	// "GenOnlineService import" converts the old production schema one way: validate writes nothing, convert and verify share one transaction
	public static class Import
	{
		private const int BatchSize = 2000;
		private const int MaxExamples = 20;
		private const string ImportActor = "import";
		private const string LegacyBanReason = "Imported legacy ban";

		private const int AccountTypeGhost = 2;
		private const int AccountTypeDevAccount = 3;

		public static ImportSettings ReadSettings(IConfiguration config)
		{
			IConfiguration section = config.GetSection("Import");
			string? provider = section.GetValue<string>("db_provider");

			if (string.IsNullOrWhiteSpace(provider))
			{
				throw new ImportException("Import:db_provider is not set. Configure the Import section (the old production database) first, see docs/schema.md.");
			}

			string normalized = provider.Trim().ToLowerInvariant();
			if (normalized != "mysql" && normalized != "mariadb")
			{
				throw new ImportException($"Unsupported Import:db_provider \"{provider}\". The old production database is MariaDB/MySQL: use \"mariadb\" or \"mysql\".");
			}

			string Require(string key)
			{
				return section.GetValue<string>(key) ?? throw new ImportException($"Import:{key} is not set.");
			}

			// Same values as the driver: None, Preferred (default: encrypt when the server can), Required, VerifyCA, VerifyFull
			string? sslText = section.GetValue<string>("db_ssl_mode");
			MySqlSslMode sslMode = MySqlSslMode.Preferred;

			if (!string.IsNullOrWhiteSpace(sslText) && !Enum.TryParse(sslText.Trim(), true, out sslMode))
			{
				throw new ImportException($"Unsupported Import:db_ssl_mode \"{sslText}\". Supported values are None, Preferred (default), Required, VerifyCA and VerifyFull.");
			}

			string legacyBans = (section.GetValue<string>("legacy_bans") ?? "local").Trim().ToLowerInvariant();
			if (legacyBans != "local" && legacyBans != "skip")
			{
				throw new ImportException($"Unsupported Import:legacy_bans \"{legacyBans}\". Use \"local\" (default) or \"skip\".");
			}

			var waivers = new HashSet<string>(StringComparer.Ordinal);
			foreach (string waiver in section.GetSection("waive").Get<string[]>() ?? Array.Empty<string>())
			{
				string key = waiver.Trim().ToLowerInvariant();
				if (!ImportFindings.All.Contains(key))
				{
					throw new ImportException($"Unknown Import:waive entry \"{waiver}\". Known findings: {string.Join(", ", ImportFindings.All)}.");
				}

				waivers.Add(key);
			}

			string? reportPath = section.GetValue<string>("report_path");
			if (string.IsNullOrWhiteSpace(reportPath))
			{
				reportPath = $"import-report-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.txt";
			}

			return new ImportSettings(
				Require("db_host"),
				section.GetValue<ushort?>("db_port") ?? 3306,
				Require("db_name"),
				Require("db_username"),
				Require("db_password"),
				sslMode,
				legacyBans == "local",
				waivers,
				reportPath,
				string.IsNullOrWhiteSpace(section.GetValue<string>("keycloak_file")) ? null : section.GetValue<string>("keycloak_file")!.Trim());
		}

		public static async Task<int> Execute(AppDbContext target, ImportSettings settings, ImportOptions options)
		{
			var report = new ImportReport();
			DateTime now = Micro(DateTime.UtcNow);

			report.Line($"GenOnlineService import, {(options.DryRun ? "dry run" : "real run")}, started {now:O}");
			report.Line($"Target: {(target.Database.IsNpgsql() ? "PostgreSQL" : "MySQL/MariaDB")}");
			report.Line($"Legacy bans: {(settings.LegacyBansAsLocal ? "imported as local play bans" : "skipped")}");
			report.Line($"Waived findings: {(settings.Waivers.Count == 0 ? "none" : string.Join(", ", settings.Waivers.OrderBy(w => w)))}");
			report.Line($"Keycloak file: {settings.KeycloakFile ?? "not requested (Import:keycloak_file)"}");

			await CheckTarget(target);

			await using var source = await OpenSource(settings);
			report.Line($"Source: {source.ServerVersion}, database {settings.Name}");

			SourceSchema schema = await SourceSchema.Read(source);
			List<OldUser> users = await LoadUsers(source, schema);
			Plan plan = Plan.Build(users, report, settings);

			// Phase 1: every mapping runs once without a writer, so the findings come from the same code that converts
			var validation = new Run(source, schema, plan, settings, report, now, writer: null);
			await validation.ConvertAll();
			plan.Findings.Print(report, settings.Waivers);

			if (plan.Findings.HasUnwaived(settings.Waivers))
			{
				report.Line();
				report.Line("Stopped after validation: resolve the findings above in the source, or waive them in Import:waive. Nothing was written.");
				report.Save(settings.ReportPath);
				return 2;
			}

			if (!options.DryRun && !options.Confirm($"Convert {plan.KeptUsers.Count} users into the target database?", "Input is not interactive. Pass --yes to import in an unattended run."))
			{
				return 1;
			}

			// Phase 2: one transaction, so a failed import leaves the target empty
			await using var transaction = await target.Database.BeginTransactionAsync();

			var writer = new TargetWriter(target);
			var conversion = new Run(source, schema, plan, settings, report, now, writer);
			await conversion.ConvertAll();
			await writer.FlushAll();
			conversion.PrintMapping();

			// Phase 3: counts and checksums of every table, then the identity sequences
			await ResetSequences(target);
			bool verified = await Verify(target, writer, report);
			verified &= await VerifySequences(target, report);

			report.Line();
			if (!verified)
			{
				await transaction.RollbackAsync();
				report.Line("VERIFICATION FAILED: the transaction was rolled back, the target is unchanged.");
				report.Save(settings.ReportPath);
				return 1;
			}

			if (options.DryRun)
			{
				await transaction.RollbackAsync();
				report.Line("Dry run complete: verification passed and the transaction was rolled back.");
			}
			else
			{
				await transaction.CommitAsync();
				report.Line("Import complete: verification passed and the transaction was committed.");
			}

			// written only after the commit, so Keycloak never holds player ids the database does not
			List<KeycloakImportUser> keycloakUsers = KeycloakUsers(plan);
			long counterStart = KeycloakImportFile.BuildCounter(keycloakUsers)[KeycloakImportFile.CounterStartAttribute]!.GetValue<long>();
			if (settings.KeycloakFile != null && !options.DryRun)
			{
				(string usersPath, string counterPath) = KeycloakImportFile.Write(settings.KeycloakFile, keycloakUsers);
				report.Line($"Keycloak file: {keycloakUsers.Count} users with player_id written to {Path.GetFullPath(usersPath)}");
				report.Line($"Keycloak player_id counter: start the GenOnline Keycloak plugin's counter at {counterStart}: set the realm attribute {KeycloakImportFile.CounterStartAttribute} to it before anyone signs up ({Path.GetFullPath(counterPath)})");
				report.Line("Keycloak name tags: after the partial import, call POST /realms/<realm>/genonline/players/assign-tags once with a client holding genonline-rename; it gives every imported account its static tag and lists name conflicts");
			}
			else if (settings.KeycloakFile != null)
			{
				report.Line($"Keycloak file: not written in a dry run; it would hold {keycloakUsers.Count} users, counter start {counterStart}");
			}

			report.Save(settings.ReportPath);
			return 0;
		}

		// Each kept user with its kept identities; the player id is the user id the import keeps
		private static List<KeycloakImportUser> KeycloakUsers(Plan plan)
		{
			return plan.Kept.OrderBy(k => k.Key)
				.Select(k => new KeycloakImportUser(k.Key,
					k.Value.Identities.Where(i => i.Kept).Select(i => new KeycloakImportIdentity(i.Provider, i.ExternUid, i.ExternUsername)).ToList(),
					k.Value.Username))
				.ToList();
		}

		private static string QuoteTable(AppDbContext db, string table)
		{
			return db.Database.IsNpgsql() ? "\"" + table.Replace("\"", "\"\"") + "\"" : "`" + table.Replace("`", "``") + "`";
		}

		private static async Task CheckTarget(AppDbContext target)
		{
			var pending = (await target.Database.GetPendingMigrationsAsync()).ToList();
			if (pending.Count > 0)
			{
				throw new ImportException($"The target database has {pending.Count} pending migration(s). Run \"GenOnlineService migrate\" first.");
			}

			var notEmpty = new List<string>();
			foreach (string table in ModelTables(target))
			{
				string alias = target.Database.IsNpgsql() ? "\"Value\"" : "`Value`";
				long count = await target.Database.SqlQueryRaw<long>($"SELECT COUNT(*) AS {alias} FROM {QuoteTable(target, table)}").SingleAsync();
				if (count > 0)
				{
					notEmpty.Add($"{table} ({count} rows)");
				}
			}

			if (notEmpty.Count > 0)
			{
				throw new ImportException($"The target database is not empty, the import only writes into an empty database. Tables with rows: {string.Join(", ", notEmpty)}.");
			}
		}

		private static IEnumerable<string> ModelTables(AppDbContext db)
		{
			return db.Model.GetEntityTypes().Select(e => e.GetTableName()!).Distinct().OrderBy(t => t, StringComparer.Ordinal);
		}

		private static readonly string[] IdentityTables =
		{
			"users", "user_username_history", "identities", "sanctions", "audit_log", "devices", "session_events", "matches",
			"anticheat_module_reports", "anticheat_new_account_games", "anticheat_probe_reports"
		};

		private static string IdentityColumn(string table) => table.StartsWith("anticheat_", StringComparison.Ordinal) ? "report_id" : "id";

		// PostgreSQL identities do not follow explicit ids; MySQL AUTO_INCREMENT already moved past them on insert
		private static async Task ResetSequences(AppDbContext db)
		{
			if (!db.Database.IsNpgsql())
			{
				return;
			}

			foreach (string table in IdentityTables)
			{
				string column = IdentityColumn(table);
				await db.Database.ExecuteSqlRawAsync(
					$"SELECT setval(pg_get_serial_sequence('{table}', '{column}'), COALESCE((SELECT MAX({column}) FROM \"{table}\"), 0) + 1, false)");
			}
		}

		private static async Task<bool> VerifySequences(AppDbContext db, ImportReport report)
		{
			report.Line();
			report.Line("Identity sequences (next id must be max(id) + 1 or higher):");

			bool ok = true;
			foreach (string table in IdentityTables)
			{
				string column = IdentityColumn(table);
				string quoted = QuoteTable(db, table);
				string alias = db.Database.IsNpgsql() ? "\"Value\"" : "`Value`";

				long max = await db.Database.SqlQueryRaw<long>($"SELECT COALESCE(MAX({column}), 0) AS {alias} FROM {quoted}").SingleAsync();

				long next;
				if (db.Database.IsNpgsql())
				{
					string sequence = await db.Database.SqlQueryRaw<string>($"SELECT pg_get_serial_sequence('{table}', '{column}') AS \"Value\"").SingleAsync();
					next = await db.Database.SqlQueryRaw<long>(
						$"SELECT CASE WHEN is_called THEN last_value + 1 ELSE last_value END AS \"Value\" FROM {sequence}").SingleAsync();
				}
				else
				{
					// An insert with an explicit id moves AUTO_INCREMENT past it; SHOW TABLE STATUS reads the live counter
					next = await NextAutoIncrement(db, table);
				}

				bool tableOk = next >= max + 1;
				ok &= tableOk;
				report.Line($"  {table,-30} max {max,10}  next {next,10}  {(tableOk ? "ok" : "FAIL")}");
			}

			return ok;
		}

		private static async Task<long> NextAutoIncrement(AppDbContext db, string table)
		{
			var connection = db.Database.GetDbConnection();
			await using var command = connection.CreateCommand();
			command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
			command.CommandText = $"SHOW TABLE STATUS LIKE '{table}'";

			await using var reader = await command.ExecuteReaderAsync();
			if (!await reader.ReadAsync())
			{
				return 0;
			}

			object value = reader["Auto_increment"];
			return value is DBNull ? 1 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
		}

		private static async Task<bool> Verify(AppDbContext db, TargetWriter writer, ImportReport report)
		{
			report.Line();
			report.Line("Verification (rows written vs rows read back, SHA-256 row checksums):");

			var hashMethod = typeof(Import).GetMethod(nameof(HashTable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
			bool ok = true;

			foreach (IEntityType entityType in db.Model.GetEntityTypes().OrderBy(e => e.GetTableName(), StringComparer.Ordinal))
			{
				string table = entityType.GetTableName()!;
				var task = (Task<TableHash>)hashMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, new object[] { db, entityType })!;
				TableHash actual = await task;
				TableHash expected = writer.Expected(table);

				bool countOk = expected.Count == actual.Count;
				bool sumOk = expected.Checksum.AsSpan().SequenceEqual(actual.Checksum);
				ok &= countOk && sumOk;

				report.Line($"  {table,-30} written {expected.Count,10}  read {actual.Count,10}  checksum {(sumOk ? "match" : "MISMATCH")} {Convert.ToHexString(actual.Checksum)[..16]}{(countOk && sumOk ? "" : "  FAIL")}");
			}

			return ok;
		}

		private static async Task<TableHash> HashTable<T>(AppDbContext db, IEntityType entityType) where T : class
		{
			var hash = new TableHash();
			await foreach (T row in db.Set<T>().AsNoTracking().AsAsyncEnumerable())
			{
				hash.Add(RowHash.Of(entityType, row));
			}

			return hash;
		}

		private static async Task<MySqlConnection> OpenSource(ImportSettings settings)
		{
			var builder = new MySqlConnectionStringBuilder
			{
				Server = settings.Host,
				Port = settings.Port,
				Database = settings.Name,
				UserID = settings.Username,
				Password = settings.Password,
				SslMode = settings.SslMode,
				ConvertZeroDateTime = true,
				DefaultCommandTimeout = 0,
				Pooling = false
			};

			var connection = new MySqlConnection(builder.ConnectionString);
			await connection.OpenAsync();

			// One read-only snapshot, so validation and conversion see the same data
			await using (var command = new MySqlCommand("SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ", connection))
			{
				await command.ExecuteNonQueryAsync();
			}

			await using (var command = new MySqlCommand("START TRANSACTION WITH CONSISTENT SNAPSHOT, READ ONLY", connection))
			{
				await command.ExecuteNonQueryAsync();
			}

			return connection;
		}

		private static async Task<List<OldUser>> LoadUsers(MySqlConnection source, SourceSchema schema)
		{
			var users = new List<OldUser>();

			string sql = "SELECT user_id, account_type, steam_id, discord_id, discord_username, gamereplays_id, gamereplays_username, displayname, lastlogin, "
				+ "favorite_color, favorite_side, favorite_map, favorite_starting_money, favorite_limit_superweapons, admin, banned, "
				+ "elo_rating, monthly_elo_rating, elo_num_matches, ban_reason, banned_by, ban_verified_by, ban_aliases, "
				+ $"{schema.Optional("users", "google_id")}, {schema.Optional("users", "google_email")}, {schema.Optional("users", "permissionslevel")}, "
				+ $"{schema.Optional("users", "relay_region")}, {schema.Optional("users", "portmapping_tech")} "
				+ "FROM users ORDER BY user_id";

			await foreach (var r in Rows(source, sql))
			{
				users.Add(new OldUser
				{
					Id = r.Long("user_id"),
					AccountType = (int)r.Long("account_type"),
					SteamId = r.NullableLong("steam_id"),
					DiscordId = r.NullableLong("discord_id"),
					DiscordUsername = r.String("discord_username"),
					GameReplaysId = r.NullableLong("gamereplays_id"),
					GameReplaysUsername = r.String("gamereplays_username"),
					DisplayName = r.String("displayname"),
					LastLogin = r.NullableDate("lastlogin"),
					FavoriteColor = r.NullableLong("favorite_color"),
					FavoriteSide = r.NullableLong("favorite_side"),
					FavoriteMap = r.String("favorite_map"),
					FavoriteStartingMoney = r.NullableLong("favorite_starting_money"),
					FavoriteLimitSuperweapons = r.NullableLong("favorite_limit_superweapons"),
					Admin = r.Long("admin") != 0,
					Banned = r.Long("banned") != 0,
					EloRating = (int)r.Long("elo_rating"),
					MonthlyEloRating = (int)r.Long("monthly_elo_rating"),
					EloMatches = (int)r.Long("elo_num_matches"),
					BanReason = r.String("ban_reason"),
					BannedBy = r.String("banned_by"),
					BanVerifiedBy = r.String("ban_verified_by"),
					BanAliases = r.String("ban_aliases"),
					GoogleId = r.String("google_id"),
					GoogleEmail = r.String("google_email"),
					PermissionsLevel = r.NullableLong("permissionslevel"),
					RelayRegion = r.NullableLong("relay_region"),
					PortmappingTech = r.NullableLong("portmapping_tech")
				});
			}

			return users;
		}

		internal static async IAsyncEnumerable<SourceRow> Rows(MySqlConnection source, string sql)
		{
			await using var command = new MySqlCommand(sql, source);
			await using var reader = await command.ExecuteReaderAsync();
			var row = new SourceRow(reader);

			while (await reader.ReadAsync())
			{
				yield return row;
			}
		}

		internal static async Task<long> Count(MySqlConnection source, string table)
		{
			await using var command = new MySqlCommand($"SELECT COUNT(*) FROM `{table}`", source);
			return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
		}

		// Timestamps keep microseconds, the precision both targets store
		internal static DateTime Micro(DateTime value)
		{
			return new DateTime(value.Ticks - (value.Ticks % 10), DateTimeKind.Utc);
		}

		// One pass over every mapping; without a writer it only validates and counts
		private sealed class Run
		{
			private readonly MySqlConnection _source;
			private readonly SourceSchema _schema;
			private readonly Plan _plan;
			private readonly ImportSettings _settings;
			private readonly ImportReport _report;
			private readonly DateTime _now;
			private readonly TargetWriter? _writer;
			private readonly bool _postgresTarget;
			private readonly List<MappingLine> _lines = new();
			private readonly HashSet<long> _importedMatches = new();
			private readonly NoteCounter _notes;

			public Run(MySqlConnection source, SourceSchema schema, Plan plan, ImportSettings settings, ImportReport report, DateTime now, TargetWriter? writer)
			{
				_source = source;
				_schema = schema;
				_plan = plan;
				_settings = settings;
				_report = report;
				_now = now;
				_writer = writer;
				_postgresTarget = writer?.IsPostgres ?? false;
				_notes = new NoteCounter(plan.Notes);
			}

			private Findings Findings => _plan.Findings;

			private MappingLine Line(string from, string to)
			{
				var line = new MappingLine(from, to);
				_lines.Add(line);
				return line;
			}

			private void Write(MappingLine line, object entity)
			{
				line.Written++;
				_writer?.Add(entity);
			}

			// Dropped user rows are expected; a user id nobody knows is an orphan
			private bool KeepUserRow(MappingLine line, long userId, string table)
			{
				if (_plan.Kept.ContainsKey(userId))
				{
					return true;
				}

				if (_plan.Dropped.Contains(userId))
				{
					line.Drop("user not imported (Ghost/DevAccount)");
					return false;
				}

				line.Drop("orphaned");
				Findings.Add(ImportFindings.OrphanedRows, $"{table}: user {userId} does not exist");
				return false;
			}

			// A user id that may be kept, or nulled when its user is not imported
			private long? OptionalUser(long? userId, string table, string what)
			{
				if (userId == null || userId <= 0)
				{
					return null;
				}

				if (_plan.Kept.ContainsKey(userId.Value))
				{
					return userId;
				}

				if (!_plan.Dropped.Contains(userId.Value))
				{
					Findings.Add(ImportFindings.OrphanedRows, $"{table}: {what} {userId} does not exist");
				}

				return null;
			}

			private string Clamp(string? value, int maxLength, string table, string column)
			{
				string clean = TextSanitizer.Clean(value, maxLength);
				if (!string.Equals(clean, value ?? string.Empty, StringComparison.Ordinal))
				{
					_notes.Add($"{table}.{column}: value cut to {maxLength} characters or NUL removed");
				}

				return clean;
			}

			private string? ClampOptional(string? value, int maxLength, string table, string column)
			{
				return string.IsNullOrEmpty(value) ? null : Clamp(value, maxLength, table, column);
			}

			// jsonb refuses \u0000; it can only come from a NUL that no reader needs
			private string? CleanJson(string? json)
			{
				if (json == null || !_postgresTarget || !json.Contains("\\u0000", StringComparison.Ordinal))
				{
					return json;
				}

				_notes.Add("JSON values: \\u0000 escapes removed for PostgreSQL");
				return json.Replace("\\u0000", string.Empty, StringComparison.Ordinal);
			}

			private DateTime ValidDate(DateTime? value, string table, string column, long id)
			{
				if (value == null || value.Value.Year < 1970)
				{
					Findings.Add(ImportFindings.InvalidValues, $"{table}.{column} of {id}: {(value == null ? "zero date" : value.Value.ToString("O"))}, the Unix epoch is used");
					return DateTime.UnixEpoch;
				}

				return Micro(value.Value);
			}

			public async Task ConvertAll()
			{
				ConvertUsers();
				await ConvertStats();
				await ConvertDevices();
				await ConvertSocial("friends", "user_id_1", "user_id_2", "friendships", (a, b) =>
				{
					long low = Math.Min(a, b), high = Math.Max(a, b);
					return ($"{low}:{high}", new Friendship { UserIdLow = low, UserIdHigh = high, CreatedAt = _now });
				});
				await ConvertSocial("friends_requests", "source_user_id", "target_user_id", "friend_requests",
					(a, b) => ($"{a}:{b}", new FriendRequest { FromUserId = a, ToUserId = b, CreatedAt = _now }));
				await ConvertSocial("friends_blocked", "source_user_id", "target_user_id", "blocks",
					(a, b) => ($"{a}:{b}", new UserBlock { UserId = a, BlockedUserId = b, CreatedAt = _now }));
				await ConvertMatches();
				await ConvertPublications();
				await ConvertLeaderboards();
				await ConvertAnticheat();

				_writer?.Flush();
				if (_writer != null)
				{
					await _writer.FlushAll();
				}
			}

			private void ConvertUsers()
			{
				var users = Line("users (Steam, Discord, unknown)", "users");
				var steam = Line("users.steam_id", "identities (steam)");
				var discord = Line("users.discord_id, discord_username", "identities (discord)");
				var google = Line("users.google_id", "identities (google)");
				var gameReplays = Line("users.gamereplays_id, gamereplays_username", "identities (gamereplays)");
				var bans = Line("users.banned, ban_reason", "sanctions (ban, local)");
				var audit = Line("users.banned_by, ban_verified_by, ban_aliases", "audit_log (ban)");
				var allTime = Line("users.elo_rating, elo_num_matches", "player_ratings (all_time)");
				var monthly = Line("users.monthly_elo_rating", "player_ratings (monthly)");
				var preferences = Line("users.favorite_*", "player_preferences");
				var network = Line("users.relay_region, portmapping_tech", "player_network");

				foreach (OldUser old in _plan.AllUsers)
				{
					users.Source++;

					if (!_plan.Kept.TryGetValue(old.Id, out KeptUser? kept))
					{
						users.Drop(_plan.DropReason(old));
						continue;
					}

					Write(users, new User
					{
						ID = old.Id,
						Username = kept.Username,
						UsernameNormalized = NameNormalizer.Normalize(kept.Username),
						// imported names are unique already (AssignUsernames), so tag 1 keeps name#tag unique; Keycloak's tag replaces it at sign-in
						NameTag = 1,
						Email = kept.Email,
						EmailNormalized = kept.Email == null ? null : NameNormalizer.Normalize(kept.Email),
						State = UserStates.Active,
						CreatedAt = _now,
						UpdatedAt = _now,
						LastSignInAt = old.LastLogin is { Year: >= 1970 } lastLogin ? Micro(lastLogin) : null
					});

					foreach (KeptIdentity identity in kept.Identities)
					{
						MappingLine line = identity.Provider switch
						{
							IdentityProviders.Steam => steam,
							IdentityProviders.Discord => discord,
							IdentityProviders.Google => google,
							_ => gameReplays
						};

						line.Source++;
						if (!identity.Kept)
						{
							line.Drop("duplicate identity, kept on another user");
							continue;
						}

						Write(line, new UserIdentity
						{
							UserID = old.Id,
							Provider = identity.Provider,
							ExternUid = identity.ExternUid,
							ExternUsername = ClampOptional(identity.ExternUsername, 128, "identities", "extern_username"),
							CreatedAt = _now
						});
					}

					if (old.Banned)
					{
						bans.Source++;
						audit.Source++;

						if (!_settings.LegacyBansAsLocal)
						{
							bans.Drop("legacy bans skipped by Import:legacy_bans");
							audit.Drop("legacy bans skipped by Import:legacy_bans");
						}
						else
						{
							string reason = string.IsNullOrWhiteSpace(old.BanReason) ? LegacyBanReason : Clamp(old.BanReason, ModerationManager.MaximumReasonLength, "sanctions", "reason");

							Write(bans, new Sanction
							{
								UserID = old.Id,
								Type = SanctionTypes.Ban,
								Source = AuthoritySources.Local,
								Reason = reason,
								CreatedByKind = AuditActorKinds.Cli,
								CreatedAt = _now
							});

							Write(audit, new AuditLogEntry
							{
								CreatedAt = _now,
								ActorKind = AuditActorKinds.Cli,
								Actor = ImportActor,
								Action = ModerationActions.Ban,
								TargetKind = AuditTargetKinds.User,
								TargetID = old.Id,
								Reason = reason,
								Source = ImportActor,
								Details = JsonSerializer.Serialize(new
								{
									legacy = true,
									banned_by = old.BannedBy ?? string.Empty,
									ban_verified_by = old.BanVerifiedBy ?? string.Empty,
									ban_aliases = old.BanAliases ?? string.Empty
								})
							});
						}
					}

					allTime.Source++;
					Write(allTime, new PlayerRating { UserID = old.Id, Kind = RatingKinds.AllTime, Rating = old.EloRating, MatchesPlayed = old.EloMatches, UpdatedAt = _now });

					monthly.Source++;
					Write(monthly, new PlayerRating { UserID = old.Id, Kind = RatingKinds.Monthly, Rating = old.MonthlyEloRating, MatchesPlayed = 0, UpdatedAt = _now });

					// -1 (and an empty map) meant "never chosen"
					var preference = new PlayerPreference
					{
						UserID = old.Id,
						Color = old.FavoriteColor is null or -1 ? null : (int)old.FavoriteColor,
						Side = old.FavoriteSide is null or -1 ? null : (int)old.FavoriteSide,
						Map = string.IsNullOrEmpty(old.FavoriteMap) ? null : Clamp(old.FavoriteMap, PlayerPreference.MapMaxLength, "player_preferences", "map"),
						StartingMoney = old.FavoriteStartingMoney is null or -1 ? null : (int)old.FavoriteStartingMoney,
						LimitSuperweapons = old.FavoriteLimitSuperweapons is null or < 0 ? null : old.FavoriteLimitSuperweapons > 0,
						UpdatedAt = _now
					};

					preferences.Source++;
					if (preference.Color == null && preference.Side == null && preference.Map == null && preference.StartingMoney == null && preference.LimitSuperweapons == null)
					{
						preferences.Drop("nothing chosen");
					}
					else
					{
						Write(preferences, preference);
					}

					// relay_region -1 and portmapping_tech -2 meant "unset"; the old ipv4/ipv6 flags hold no address
					network.Source++;
					string? relay = old.RelayRegion is null or -1 ? null : old.RelayRegion.Value.ToString(CultureInfo.InvariantCulture);
					string? portMapping = old.PortmappingTech is null or -2 ? null : old.PortmappingTech.Value.ToString(CultureInfo.InvariantCulture);
					if (relay == null && portMapping == null)
					{
						network.Drop("nothing set");
					}
					else
					{
						Write(network, new PlayerNetwork { UserID = old.Id, RelayRegion = relay, PortMapping = portMapping, UpdatedAt = _now });
					}

					_writer?.MaybeFlush();
				}
			}

			private async Task ConvertStats()
			{
				var line = Line("user_stats_v2", "player_stats");

				await foreach (var r in Rows(_source, "SELECT user_id, stats FROM user_stats_v2 ORDER BY user_id"))
				{
					line.Source++;
					long userId = r.Long("user_id");
					if (!KeepUserRow(line, userId, "user_stats_v2"))
						continue;

					string? stats = CleanJson(r.String("stats"));
					if (!IsJsonObject(stats))
					{
						line.Drop("invalid JSON");
						Findings.Add(ImportFindings.InvalidJson, $"user_stats_v2: stats of user {userId} is not a JSON object");
						continue;
					}

					Write(line, new PlayerStat { UserId = userId, Stats = stats!, UpdatedAt = _now });
					_writer?.MaybeFlush();
				}
			}

			private async Task ConvertDevices()
			{
				var line = Line("user_devices.hwid_0..2, ip_addr", "devices");
				var seen = new Dictionary<(long, string, string), Device>();

				await foreach (var r in Rows(_source, "SELECT user_id, hwid_0, hwid_1, hwid_2, ip_addr FROM user_devices ORDER BY user_id, hwid_0, hwid_1, hwid_2, ip_addr"))
				{
					long userId = r.Long("user_id");
					string ip = r.String("ip_addr") ?? string.Empty;

					foreach (var (column, kind) in new[] { ("hwid_0", DeviceKinds.MachineGuid), ("hwid_1", DeviceKinds.Mac), ("hwid_2", DeviceKinds.VolumeSerial) })
					{
						line.Source++;
						if (!KeepUserRow(line, userId, "user_devices"))
							continue;

						string md5 = (r.String(column) ?? string.Empty).Trim().ToUpperInvariant();
						if (md5.Length == 0 || md5 == DeviceHasher.NoneMd5)
						{
							line.Drop("value missing (NONE)");
							continue;
						}

						// Rows differed by IP address; one device row per user, kind and value keeps the last IP
						if (seen.TryGetValue((userId, kind, md5), out Device? existing))
						{
							line.Drop("same device on another IP");
							existing.LastIp = string.IsNullOrEmpty(ip) ? existing.LastIp : Clamp(ip, 45, "devices", "last_ip");
							continue;
						}

						DateTime lastSeen = _plan.Kept[userId].LastSeen ?? _now;
						var device = new Device
						{
							UserID = userId,
							Kind = kind,
							ValueHmac = DeviceHasher.HashLegacyMd5(md5),
							FirstSeenAt = lastSeen,
							LastSeenAt = lastSeen,
							LastIp = string.IsNullOrEmpty(ip) ? null : Clamp(ip, 45, "devices", "last_ip")
						};

						seen[(userId, kind, md5)] = device;
					}
				}

				// Written after the scan, once the last IP of every device is known
				foreach (Device device in seen.Values)
				{
					Write(line, device);
					_writer?.MaybeFlush();
				}
			}

			private async Task ConvertSocial<T>(string table, string firstColumn, string secondColumn, string target, Func<long, long, (string Key, T Row)> map) where T : class
			{
				var line = Line(table, target);
				var keys = new HashSet<string>(StringComparer.Ordinal);

				await foreach (var r in Rows(_source, $"SELECT {firstColumn}, {secondColumn} FROM {table} ORDER BY {firstColumn}, {secondColumn}"))
				{
					line.Source++;
					long a = r.Long(firstColumn);
					long b = r.Long(secondColumn);

					if (!KeepUserRow(line, a, table) || !KeepUserRow(line, b, table))
						continue;

					if (a == b)
					{
						line.Drop("same user on both sides");
						continue;
					}

					var (key, row) = map(a, b);
					if (!keys.Add(key))
					{
						line.Drop("duplicate pair");
						continue;
					}

					Write(line, row);
					_writer?.MaybeFlush();
				}
			}

			private async Task ConvertMatches()
			{
				var matches = Line("match_history", "matches");
				var players = Line("match_history.member_slot_0..7", "match_players");

				string sql = "SELECT match_id, owner, name, finished, started, time_finished, map_name, map_official, match_roster_type, lobby_type, "
					+ "vanilla_teams, starting_cash, limit_superweapons, track_stats, allow_observers, max_cam_height, exe_crc, ini_crc, map_path, "
					+ "member_slot_0, member_slot_1, member_slot_2, member_slot_3, member_slot_4, member_slot_5, member_slot_6, member_slot_7 "
					+ "FROM match_history ORDER BY match_id";

				await foreach (var r in Rows(_source, sql))
				{
					matches.Source++;
					long id = r.Long("match_id");

					if (id <= 0)
					{
						matches.Drop("invalid id");
						Findings.Add(ImportFindings.InvalidValues, $"match_history: match id {id}");
						continue;
					}

					bool finished = r.Long("finished") != 0;
					DateTime started = ValidDate(r.NullableDate("started"), "match_history", "started", id);
					long? lobbyType = r.NullableLong("lobby_type");
					ELobbyType? type = lobbyType switch
					{
						null => null,
						0 => ELobbyType.CustomGame,
						1 => ELobbyType.QuickMatch,
						_ => null
					};

					if (lobbyType != null && type == null)
					{
						Findings.Add(ImportFindings.InvalidValues, $"match_history.lobby_type of {id}: {lobbyType}, stored as null");
					}

					var match = new Match
					{
						Id = id,
						OwnerUserId = OptionalUser(r.NullableLong("owner"), "match_history", "owner"),
						Name = Clamp(r.String("name"), Match.NameMaxLength, "matches", "name"),
						LobbyType = type,
						RosterType = Clamp(r.String("match_roster_type"), 32, "matches", "roster_type"),
						MapName = Clamp(r.String("map_name"), Match.MapNameMaxLength, "matches", "map_name"),
						MapPath = ClampOptional(r.String("map_path"), Match.MapPathMaxLength, "matches", "map_path"),
						IsOfficialMap = r.Long("map_official") != 0,
						VanillaTeams = r.Long("vanilla_teams") != 0,
						StartingCash = (uint)r.Long("starting_cash"),
						LimitSuperweapons = r.Long("limit_superweapons") != 0,
						TrackStats = r.Long("track_stats") != 0,
						AllowObservers = r.Long("allow_observers") != 0,
						MaxCamHeight = (ushort)r.Long("max_cam_height"),
						ExeCrc = (uint)r.Long("exe_crc"),
						IniCrc = (uint)r.Long("ini_crc"),
						StartedAt = started,
						FinishedAt = finished ? ValidDate(r.NullableDate("time_finished"), "match_history", "time_finished", id) : null
					};

					for (int slot = 0; slot < 8; slot++)
					{
						string? json = CleanJson(r.String($"member_slot_{slot}"));
						if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
							continue;

						players.Source++;

						MatchdataMemberModel member;
						try
						{
							member = JsonSerializer.Deserialize<MatchdataMemberModel>(json);
						}
						catch (JsonException)
						{
							players.Drop("invalid JSON");
							Findings.Add(ImportFindings.InvalidJson, $"match_history.member_slot_{slot} of match {id}");
							continue;
						}

						var details = new MatchPlayerDetails
						{
							display_name = member.display_name ?? string.Empty,
							slot_state = member.slot_state,
							startpos = member.startpos,
							buildings_built = member.buildings_built,
							buildings_killed = member.buildings_killed,
							buildings_lost = member.buildings_lost,
							units_built = member.units_built,
							units_killed = member.units_killed,
							units_lost = member.units_lost,
							total_money = member.total_money,
							desynced = member.desynced,
							duration = member.duration,
							metadata = member.metadata ?? new List<MemberMetadataModel>()
						};

						// A finished non-observer without a win lost; unfinished matches have no result yet
						string? result = member.won ? MatchResults.Win
							: finished && member.side != Constants.OBSERVER_SIDE_VALUE ? MatchResults.Loss
							: null;

						var player = new MatchPlayer
						{
							MatchId = id,
							Slot = (short)slot,
							UserId = OptionalUser(member.user_id, "match_history", $"member_slot_{slot} user"),
							Team = (short)member.team,
							Side = member.side,
							Color = member.color,
							Result = result,
							Details = MatchHistory.WriteDetails(details)
						};

						match.Players.Add(player);
						players.Written++;
					}

					_importedMatches.Add(id);
					Write(matches, match);
					_writer?.MaybeFlush();
				}
			}

			private async Task ConvertPublications()
			{
				var line = Line("external_publication", "match_publications");

				await foreach (var r in Rows(_source, "SELECT match_id, next_attempt_at, published_at, attempts, last_error FROM external_publication ORDER BY match_id"))
				{
					line.Source++;
					long matchId = r.Long("match_id");

					if (!_importedMatches.Contains(matchId))
					{
						line.Drop("orphaned");
						Findings.Add(ImportFindings.OrphanedRows, $"external_publication: match {matchId} does not exist");
						continue;
					}

					DateTime? next = r.NullableDate("next_attempt_at");
					DateTime? published = r.NullableDate("published_at");

					Write(line, new MatchPublication
					{
						MatchId = matchId,
						NextAttemptAt = next is { Year: >= 1970 } ? Micro(next.Value) : null,
						PublishedAt = published is { Year: >= 1970 } ? Micro(published.Value) : null,
						Attempts = (int)r.Long("attempts"),
						LastError = ClampOptional(r.String("last_error"), 512, "match_publications", "last_error")
					});
					_writer?.MaybeFlush();
				}
			}

			private async Task ConvertLeaderboards()
			{
				await ConvertLeaderboard("leaderboard_daily", "day_of_year", LeaderboardPeriods.Day, (year, unit) =>
					unit >= 1 && unit <= (DateTime.IsLeapYear(year) ? 366 : 365) ? new DateOnly(year, 1, 1).AddDays(unit - 1) : null);
				await ConvertLeaderboard("leaderboard_monthly", "month_of_year", LeaderboardPeriods.Month, (year, unit) =>
					unit >= 1 && unit <= 12 ? new DateOnly(year, unit, 1) : null);
				await ConvertLeaderboard("leaderboard_yearly", null, LeaderboardPeriods.Year, (year, _) => new DateOnly(year, 1, 1));
			}

			private async Task ConvertLeaderboard(string table, string? unitColumn, string period, Func<int, int, DateOnly?> start)
			{
				var line = Line(table, $"leaderboard_entries ({period})");
				string unit = unitColumn ?? "0";

				await foreach (var r in Rows(_source, $"SELECT user_id, year, {unit} AS unit, points, wins, losses FROM {table} ORDER BY user_id, year, unit"))
				{
					line.Source++;
					long userId = r.Long("user_id");
					if (!KeepUserRow(line, userId, table))
						continue;

					int year = (int)r.Long("year");
					int unitValue = (int)r.Long("unit");
					DateOnly? periodStart = year >= 1970 && year <= 9999 ? start(year, unitValue) : null;

					if (periodStart == null)
					{
						line.Drop("invalid period");
						Findings.Add(ImportFindings.InvalidValues, $"{table}: user {userId} year {year} {unitColumn ?? ""} {unitValue}".TrimEnd());
						continue;
					}

					Write(line, new LeaderboardEntry
					{
						Period = period,
						PeriodStart = periodStart.Value,
						UserId = userId,
						Points = (int?)r.NullableLong("points") ?? EloConfig.BaseRating,
						Wins = (int?)r.NullableLong("wins") ?? 0,
						Losses = (int?)r.NullableLong("losses") ?? 0
					});
					_writer?.MaybeFlush();
				}
			}

			private async Task ConvertAnticheat()
			{
				var modules = Line("ac_reviews_modules", "anticheat_module_reports");
				await foreach (var r in Rows(_source, "SELECT report_id, user_id, match_id, module_name, module_path, module_size FROM ac_reviews_modules ORDER BY report_id"))
				{
					modules.Source++;
					if (!ValidReport(modules, r, "ac_reviews_modules", out long id, out long userId, out long matchId))
						continue;

					Write(modules, new AnticheatModuleReport
					{
						ReportId = id,
						UserId = userId,
						MatchId = matchId,
						ModuleName = Clamp(r.String("module_name"), AntiCheat.ModuleNameMaxLength, "anticheat_module_reports", "module_name"),
						ModulePath = Clamp(r.String("module_path"), AntiCheat.ModulePathMaxLength, "anticheat_module_reports", "module_path"),
						ModuleSize = r.Long("module_size")
					});
					_writer?.MaybeFlush();
				}

				var games = Line("ac_reviews_new_account_games", "anticheat_new_account_games");
				await foreach (var r in Rows(_source, "SELECT report_id, user_id, match_id FROM ac_reviews_new_account_games ORDER BY report_id"))
				{
					games.Source++;
					if (!ValidReport(games, r, "ac_reviews_new_account_games", out long id, out long userId, out long matchId))
						continue;

					Write(games, new AnticheatNewAccountGame { ReportId = id, UserId = userId, MatchId = matchId });
					_writer?.MaybeFlush();
				}

				var probes = Line("ac_reviews_probes", "anticheat_probe_reports");
				await foreach (var r in Rows(_source, "SELECT report_id, user_id, match_id, reason FROM ac_reviews_probes ORDER BY report_id"))
				{
					probes.Source++;
					if (!ValidReport(probes, r, "ac_reviews_probes", out long id, out long userId, out long matchId))
						continue;

					Write(probes, new AnticheatProbeReport { ReportId = id, UserId = userId, MatchId = matchId, Reason = Clamp(r.String("reason"), 256, "anticheat_probe_reports", "reason") });
					_writer?.MaybeFlush();
				}
			}

			private bool ValidReport(MappingLine line, SourceRow r, string table, out long id, out long userId, out long matchId)
			{
				id = r.Long("report_id");
				userId = r.Long("user_id");
				matchId = r.Long("match_id");

				if (id <= 0 || matchId < 0)
				{
					line.Drop("invalid id");
					Findings.Add(ImportFindings.InvalidValues, $"{table}: report {id} has an id out of range");
					return false;
				}

				return KeepUserRow(line, userId, table);
			}

			public void PrintMapping()
			{
				_report.Line();
				_report.Line("Mapping (source rows, rows dropped by reason, rows written):");

				foreach (MappingLine line in _lines)
				{
					string dropped = line.Dropped.Count == 0 ? "" : "  dropped " + string.Join(", ", line.Dropped.Select(d => $"{d.Value} {d.Key}"));
					long balance = line.Source - line.Dropped.Values.Sum() - line.Written;
					_report.Line($"  {line.From,-48} -> {line.To,-30} source {line.Source,9}  written {line.Written,9}{dropped}{(balance == 0 ? "" : $"  UNBALANCED by {balance}")}");
				}

				if (_notes.Count > 0)
				{
					_report.Line();
					_report.Line("Values adjusted to fit the new columns:");
					foreach (var note in _notes.Counts.OrderBy(n => n.Key, StringComparer.Ordinal))
					{
						_report.Line($"  {note.Value,7} x {note.Key}");
					}
				}
			}
		}

		private static bool IsJsonObject(string? json)
		{
			if (string.IsNullOrWhiteSpace(json))
				return false;

			try
			{
				return JsonNode.Parse(json) is JsonObject;
			}
			catch (JsonException)
			{
				return false;
			}
		}
	}

	internal sealed class OldUser
	{
		public long Id;
		public int AccountType;
		public long? SteamId;
		public long? DiscordId;
		public string? DiscordUsername;
		public long? GameReplaysId;
		public string? GameReplaysUsername;
		public string? DisplayName;
		public DateTime? LastLogin;
		public long? FavoriteColor;
		public long? FavoriteSide;
		public string? FavoriteMap;
		public long? FavoriteStartingMoney;
		public long? FavoriteLimitSuperweapons;
		public bool Admin;
		public bool Banned;
		public int EloRating;
		public int MonthlyEloRating;
		public int EloMatches;
		public string? BanReason;
		public string? BannedBy;
		public string? BanVerifiedBy;
		public string? BanAliases;
		public string? GoogleId;
		public string? GoogleEmail;
		public long? PermissionsLevel;
		public long? RelayRegion;
		public long? PortmappingTech;

		// Ties go to the most recent sign-in, then the lowest id
		public static int Precedence(OldUser a, OldUser b)
		{
			int byLogin = Nullable.Compare(b.LastLogin, a.LastLogin);
			return byLogin != 0 ? byLogin : a.Id.CompareTo(b.Id);
		}
	}

	internal sealed class KeptIdentity
	{
		public string Provider = string.Empty;
		public string ExternUid = string.Empty;
		public string? ExternUsername;
		public bool Kept = true;
	}

	internal sealed class KeptUser
	{
		public string Username = string.Empty;
		public string? Email;
		public DateTime? LastSeen;
		public List<KeptIdentity> Identities = new();
	}

	// Decisions about users made once, before both passes: which users stay, their usernames, identities and emails
	internal sealed class Plan
	{
		public List<OldUser> AllUsers = new();
		public Dictionary<long, KeptUser> Kept = new();
		public HashSet<long> Dropped = new();
		public Findings Findings = new();
		public NoteCounter Notes = new();
		public Dictionary<long, OldUser> KeptUsers = new();

		public string DropReason(OldUser user) => user.AccountType switch
		{
			2 => "Ghost",
			3 => "DevAccount",
			_ => "invalid id"
		};

		public static Plan Build(List<OldUser> users, ImportReport report, ImportSettings settings)
		{
			var plan = new Plan { AllUsers = users };

			foreach (OldUser user in users)
			{
				if (user.AccountType == 2 || user.AccountType == 3)
				{
					plan.Dropped.Add(user.Id);
					continue;
				}

				if (user.Id <= 0)
				{
					plan.Dropped.Add(user.Id);
					plan.Findings.Add(ImportFindings.InvalidValues, $"users: user id {user.Id}");
					continue;
				}

				plan.KeptUsers[user.Id] = user;
				plan.Kept[user.Id] = new KeptUser { LastSeen = user.LastLogin is { Year: >= 1970 } seen ? Import.Micro(seen) : null };
			}

			plan.AssignUsernames();
			plan.AssignIdentities();
			plan.AssignEmails();
			plan.PrintUserReport(report, settings);
			return plan;
		}

		private static bool IsValidUsername(string? name)
		{
			if (string.IsNullOrWhiteSpace(name) || name.Contains('\0') || name.Length > User.UsernameMaxLength)
				return false;

			return NameNormalizer.Normalize(name).Length <= User.UsernameMaxLength;
		}

		private void AssignUsernames()
		{
			var taken = new HashSet<string>(StringComparer.Ordinal);
			var needsName = new List<OldUser>();

			// Valid names first, so a generated name can never take a real one
			var groups = KeptUsers.Values
				.Where(u => IsValidUsername(u.DisplayName))
				.GroupBy(u => NameNormalizer.Normalize(u.DisplayName!));

			foreach (var group in groups)
			{
				var ordered = group.OrderBy(u => u, Comparer<OldUser>.Create(OldUser.Precedence)).ToList();
				Kept[ordered[0].Id].Username = ordered[0].DisplayName!;
				taken.Add(group.Key);

				if (ordered.Count > 1)
				{
					Findings.Add(ImportFindings.UsernameCollisions,
						$"\"{group.Key}\": users {string.Join(", ", ordered.Select(u => u.Id))}; {ordered[0].Id} keeps it");
					needsName.AddRange(ordered.Skip(1));
				}
			}

			foreach (OldUser user in KeptUsers.Values.Where(u => !IsValidUsername(u.DisplayName)))
			{
				Findings.Add(ImportFindings.InvalidUsernames, $"user {user.Id}: {(user.DisplayName == null ? "no name" : $"\"{user.DisplayName.Replace("\0", "\\0")}\"")}");
				needsName.Add(user);
			}

			foreach (OldUser user in needsName.OrderBy(u => u.Id))
			{
				string suffix = "_" + user.Id.ToString(CultureInfo.InvariantCulture);
				string stem = IsValidUsername(user.DisplayName) ? user.DisplayName! : "player";
				string name = TextSanitizer.Clean(stem, User.UsernameMaxLength - suffix.Length) + suffix;

				for (int attempt = 2; !taken.Add(NameNormalizer.Normalize(name)); attempt++)
				{
					string more = suffix + "_" + attempt.ToString(CultureInfo.InvariantCulture);
					name = TextSanitizer.Clean(stem, User.UsernameMaxLength - more.Length) + more;
				}

				Kept[user.Id].Username = name;
			}
		}

		private void AssignIdentities()
		{
			var all = new List<(OldUser User, KeptIdentity Identity)>();

			foreach (OldUser user in KeptUsers.Values)
			{
				void Link(string provider, string? uid, string? username)
				{
					if (string.IsNullOrWhiteSpace(uid))
						return;

					var identity = new KeptIdentity { Provider = provider, ExternUid = uid.Trim(), ExternUsername = string.IsNullOrEmpty(username) ? null : username };
					Kept[user.Id].Identities.Add(identity);
					all.Add((user, identity));
				}

				Link(IdentityProviders.Steam, user.SteamId > 0 ? user.SteamId.Value.ToString(CultureInfo.InvariantCulture) : null, null);
				Link(IdentityProviders.Discord, user.DiscordId > 0 ? user.DiscordId.Value.ToString(CultureInfo.InvariantCulture) : null, user.DiscordUsername);
				Link(IdentityProviders.Google, user.GoogleId, null);
				Link(IdentityProviders.GameReplays, user.GameReplaysId > 0 ? user.GameReplaysId.Value.ToString(CultureInfo.InvariantCulture) : null, user.GameReplaysUsername);
			}

			// the target allows one identity per provider per user; a second one on a user is reported and not kept
			foreach (var group in all.GroupBy(x => (x.User.Id, x.Identity.Provider)).Where(g => g.Count() > 1))
			{
				foreach (var extra in group.Skip(1))
				{
					extra.Identity.Kept = false;
				}

				Findings.Add(ImportFindings.DuplicateIdentities,
					$"user {group.Key.Id} has {group.Count()} {group.Key.Provider} identities ({string.Join(", ", group.Select(x => x.Identity.ExternUid))}); {group.First().Identity.ExternUid} is kept");
			}

			foreach (var group in all.GroupBy(x => (x.Identity.Provider, x.Identity.ExternUid)).Where(g => g.Count() > 1))
			{
				var ordered = group.OrderBy(x => x.User, Comparer<OldUser>.Create(OldUser.Precedence)).ToList();
				foreach (var loser in ordered.Skip(1))
				{
					loser.Identity.Kept = false;
				}

				Findings.Add(ImportFindings.DuplicateIdentities,
					$"{group.Key.Provider} {group.Key.ExternUid}: users {string.Join(", ", ordered.Select(x => x.User.Id))}; {ordered[0].User.Id} keeps it");
			}
		}

		private void AssignEmails()
		{
			var candidates = new List<(OldUser User, string Email, string Normalized)>();

			foreach (OldUser user in KeptUsers.Values)
			{
				string? email = user.GoogleEmail?.Trim();
				if (string.IsNullOrEmpty(email))
					continue;

				if (!email.Contains('@') || email.Length > User.EmailMaxLength || email.Contains('\0') || NameNormalizer.Normalize(email).Length > User.EmailMaxLength)
				{
					Notes.Add("users.email: invalid Google email not imported");
					continue;
				}

				candidates.Add((user, email, NameNormalizer.Normalize(email)));
			}

			foreach (var group in candidates.GroupBy(c => c.Normalized))
			{
				var ordered = group.OrderBy(c => c.User, Comparer<OldUser>.Create(OldUser.Precedence)).ToList();
				Kept[ordered[0].User.Id].Email = ordered[0].Email;

				if (ordered.Count > 1)
				{
					Findings.Add(ImportFindings.DuplicateEmails, $"{group.Key}: users {string.Join(", ", ordered.Select(c => c.User.Id))}; {ordered[0].User.Id} keeps it");
				}
			}
		}

		private void PrintUserReport(ImportReport report, ImportSettings settings)
		{
			report.Line();
			report.Line($"Users: {AllUsers.Count} in the source, {Kept.Count} imported");
			report.Line($"  dropped Ghost:       {AllUsers.Count(u => u.AccountType == 2)}");
			report.Line($"  dropped DevAccount:  {AllUsers.Count(u => u.AccountType == 3)}");
			report.Line($"  legacy bans:         {KeptUsers.Values.Count(u => u.Banned)} ({(settings.LegacyBansAsLocal ? "imported as local play bans" : "skipped")})");

			var staff = KeptUsers.Values.Where(u => u.Admin || u.PermissionsLevel > 0).OrderBy(u => u.Id).ToList();
			var droppedStaff = AllUsers.Where(u => Dropped.Contains(u.Id) && (u.Admin || u.PermissionsLevel > 0)).OrderBy(u => u.Id).ToList();

			report.Line();
			report.Line($"Staff remap report: admin and permissionslevel are NOT imported. Grant roles again with \"GenOnlineService roles grant\" or Keycloak groups ({staff.Count} users):");
			foreach (OldUser user in staff)
			{
				report.Line($"  user {user.Id,-10} {Kept[user.Id].Username,-32} admin={(user.Admin ? 1 : 0)} permissionslevel={user.PermissionsLevel?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
			}

			foreach (OldUser user in droppedStaff)
			{
				report.Line($"  user {user.Id,-10} (not imported, {DropReason(user)}) admin={(user.Admin ? 1 : 0)} permissionslevel={user.PermissionsLevel?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
			}
		}
	}

	internal sealed class NoteCounter
	{
		public Dictionary<string, long> Counts = new(StringComparer.Ordinal);

		public NoteCounter()
		{
		}

		public NoteCounter(NoteCounter copy)
		{
			Counts = new Dictionary<string, long>(copy.Counts, StringComparer.Ordinal);
		}
		public int Count => Counts.Count;

		public void Add(string note) => Counts[note] = Counts.GetValueOrDefault(note) + 1;
	}

	// Each finding kind keeps a count and the first examples; a second pass over the same data adds nothing new
	internal sealed class Findings
	{
		private readonly Dictionary<string, (long Count, List<string> Examples, HashSet<string> Seen)> _byKind = new(StringComparer.Ordinal);

		public void Add(string kind, string detail)
		{
			if (!_byKind.TryGetValue(kind, out var entry))
			{
				entry = (0, new List<string>(), new HashSet<string>(StringComparer.Ordinal));
			}

			if (!entry.Seen.Add(detail))
				return;

			entry.Count++;
			if (entry.Examples.Count < 20)
				entry.Examples.Add(detail);

			_byKind[kind] = entry;
		}

		public bool HasUnwaived(IReadOnlySet<string> waivers) => _byKind.Keys.Any(k => !waivers.Contains(k));

		public void Print(ImportReport report, IReadOnlySet<string> waivers)
		{
			report.Line();
			report.Line(_byKind.Count == 0 ? "Validation: no findings." : "Validation findings:");

			foreach (var (kind, entry) in _byKind.OrderBy(k => k.Key, StringComparer.Ordinal))
			{
				report.Line($"  {kind}: {entry.Count} {(waivers.Contains(kind) ? "(waived)" : "(BLOCKING)")}");
				foreach (string example in entry.Examples)
				{
					report.Line($"    {example}");
				}

				if (entry.Count > entry.Examples.Count)
				{
					report.Line($"    ... and {entry.Count - entry.Examples.Count} more");
				}
			}
		}
	}

	internal sealed class MappingLine
	{
		public MappingLine(string from, string to)
		{
			From = from;
			To = to;
		}

		public string From { get; }
		public string To { get; }
		public long Source;
		public long Written;
		public Dictionary<string, long> Dropped { get; } = new(StringComparer.Ordinal);

		public void Drop(string reason) => Dropped[reason] = Dropped.GetValueOrDefault(reason) + 1;
	}

	internal sealed class ImportReport
	{
		private readonly StringBuilder _text = new();

		public void Line(string text = "")
		{
			Console.WriteLine(text);
			_text.AppendLine(text);
		}

		public void Save(string path)
		{
			File.WriteAllText(path, _text.ToString());
			Console.WriteLine($"Report written to {Path.GetFullPath(path)}");
		}
	}

	internal sealed class SourceSchema
	{
		private static readonly Dictionary<string, string[]> Required = new(StringComparer.Ordinal)
		{
			["users"] = new[] { "user_id", "account_type", "steam_id", "discord_id", "discord_username", "gamereplays_id", "gamereplays_username", "displayname", "lastlogin",
				"favorite_color", "favorite_side", "favorite_map", "favorite_starting_money", "favorite_limit_superweapons", "admin", "banned",
				"elo_rating", "monthly_elo_rating", "elo_num_matches", "ban_reason", "banned_by", "ban_verified_by", "ban_aliases" },
			["user_devices"] = new[] { "user_id", "hwid_0", "hwid_1", "hwid_2", "ip_addr" },
			["user_stats_v2"] = new[] { "user_id", "stats" },
			["friends"] = new[] { "user_id_1", "user_id_2" },
			["friends_requests"] = new[] { "source_user_id", "target_user_id" },
			["friends_blocked"] = new[] { "source_user_id", "target_user_id" },
			["match_history"] = new[] { "match_id", "owner", "name", "finished", "started", "time_finished", "map_name", "map_official", "match_roster_type", "lobby_type",
				"vanilla_teams", "starting_cash", "limit_superweapons", "track_stats", "allow_observers", "max_cam_height", "exe_crc", "ini_crc", "map_path",
				"member_slot_0", "member_slot_1", "member_slot_2", "member_slot_3", "member_slot_4", "member_slot_5", "member_slot_6", "member_slot_7" },
			["external_publication"] = new[] { "match_id", "next_attempt_at", "published_at", "attempts", "last_error" },
			["leaderboard_daily"] = new[] { "user_id", "day_of_year", "year", "points", "wins", "losses" },
			["leaderboard_monthly"] = new[] { "user_id", "month_of_year", "year", "points", "wins", "losses" },
			["leaderboard_yearly"] = new[] { "user_id", "year", "points", "wins", "losses" },
			["ac_reviews_modules"] = new[] { "report_id", "user_id", "match_id", "module_name", "module_path", "module_size" },
			["ac_reviews_new_account_games"] = new[] { "report_id", "user_id", "match_id" },
			["ac_reviews_probes"] = new[] { "report_id", "user_id", "match_id", "reason" }
		};

		private readonly Dictionary<string, HashSet<string>> _columns;

		private SourceSchema(Dictionary<string, HashSet<string>> columns)
		{
			_columns = columns;
		}

		public static async Task<SourceSchema> Read(MySqlConnection source)
		{
			var columns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

			await foreach (var r in Import.Rows(source, "SELECT table_name AS t, column_name AS c FROM information_schema.columns WHERE table_schema = DATABASE()"))
			{
				string table = r.String("t")!;
				if (!columns.TryGetValue(table, out var set))
				{
					set = new HashSet<string>(StringComparer.Ordinal);
					columns[table] = set;
				}

				set.Add(r.String("c")!);
			}

			var missing = Required
				.SelectMany(t => columns.TryGetValue(t.Key, out var have)
					? t.Value.Where(c => !have.Contains(c)).Select(c => $"{t.Key}.{c}")
					: new[] { $"{t.Key} (table)" })
				.ToList();

			if (missing.Count > 0)
			{
				throw new ImportException($"The source is not the old GenOnline schema, it lacks: {string.Join(", ", missing)}.");
			}

			return new SourceSchema(columns);
		}

		// A column the website owned; NULL when the source never had it
		public string Optional(string table, string column)
		{
			return _columns[table].Contains(column) ? column : $"NULL AS {column}";
		}
	}

	internal sealed class SourceRow
	{
		private readonly MySqlDataReader _reader;
		private Dictionary<string, int>? _ordinals;

		public SourceRow(MySqlDataReader reader)
		{
			_reader = reader;
		}

		private int Ordinal(string column)
		{
			_ordinals ??= Enumerable.Range(0, _reader.FieldCount).ToDictionary(i => _reader.GetName(i), i => i, StringComparer.OrdinalIgnoreCase);
			return _ordinals[column];
		}

		private object? Value(string column)
		{
			object value = _reader.GetValue(Ordinal(column));
			return value is DBNull ? null : value;
		}

		public long? NullableLong(string column)
		{
			return Value(column) switch
			{
				null => null,
				bool b => b ? 1 : 0,
				ulong u => checked((long)u),
				string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null,
				object o => Convert.ToInt64(o, CultureInfo.InvariantCulture)
			};
		}

		public long Long(string column) => NullableLong(column) ?? 0;

		public string? String(string column)
		{
			return Value(column) switch
			{
				null => null,
				string s => s,
				byte[] bytes => Encoding.UTF8.GetString(bytes),
				object o => Convert.ToString(o, CultureInfo.InvariantCulture)
			};
		}

		// The old service stored UTC in datetime columns without a kind; zero dates read as MinValue
		public DateTime? NullableDate(string column)
		{
			object? value = Value(column);
			if (value is not DateTime date || date == DateTime.MinValue)
				return null;

			return DateTime.SpecifyKind(date, DateTimeKind.Utc);
		}
	}

	internal sealed class TableHash
	{
		public long Count;
		public byte[] Checksum = new byte[32];

		public void Add(byte[] rowHash)
		{
			Count++;
			for (int i = 0; i < Checksum.Length; i++)
			{
				Checksum[i] ^= rowHash[i];
			}
		}
	}

	// Batches inserts and hashes every row after it is saved, when generated keys are known
	internal sealed class TargetWriter
	{
		private readonly AppDbContext _db;
		private readonly List<object> _pending = new();
		private readonly Dictionary<string, TableHash> _expected = new(StringComparer.Ordinal);
		private Task _flushing = Task.CompletedTask;

		public TargetWriter(AppDbContext db)
		{
			_db = db;
			_db.ChangeTracker.AutoDetectChangesEnabled = false;
		}

		public bool IsPostgres => _db.Database.IsNpgsql();

		public void Add(object entity)
		{
			_pending.Add(entity);
		}

		public void MaybeFlush()
		{
			if (_pending.Count >= 2000)
			{
				Flush();
			}
		}

		// Queues the pending rows; writes stay in order because each flush waits for the previous one
		public void Flush()
		{
			if (_pending.Count == 0)
				return;

			var batch = _pending.ToList();
			_pending.Clear();
			Task previous = _flushing;
			_flushing = WriteBatch(previous, batch);
		}

		public async Task FlushAll()
		{
			Flush();
			await _flushing;
		}

		private async Task WriteBatch(Task previous, List<object> batch)
		{
			await previous;

			_db.AddRange(batch);
			await _db.SaveChangesAsync();

			foreach (object entity in batch)
			{
				Hash(entity);
				if (entity is Match match)
				{
					foreach (MatchPlayer player in match.Players)
					{
						Hash(player);
					}
				}
			}

			_db.ChangeTracker.Clear();
		}

		private void Hash(object entity)
		{
			IEntityType entityType = _db.Model.FindEntityType(entity.GetType())!;
			string table = entityType.GetTableName()!;

			if (!_expected.TryGetValue(table, out TableHash? hash))
			{
				hash = new TableHash();
				_expected[table] = hash;
			}

			hash.Add(RowHash.Of(entityType, entity));
		}

		public TableHash Expected(string table) => _expected.TryGetValue(table, out TableHash? hash) ? hash : new TableHash();
	}

	// SHA-256 of a row's column values in column-name order, formatted so a value reads back identically on both providers
	internal static class RowHash
	{
		public static byte[] Of(IEntityType entityType, object entity)
		{
			var text = new StringBuilder();

			foreach (IProperty property in entityType.GetProperties().OrderBy(p => p.GetColumnName(), StringComparer.Ordinal))
			{
				object? value = property.PropertyInfo?.GetValue(entity);
				string? columnType = property.GetColumnType();
				bool isJson = columnType == "json" || columnType == "jsonb";

				text.Append(property.GetColumnName()).Append('=').Append(Format(value, isJson)).Append('\u001f');
			}

			return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
		}

		private static string Format(object? value, bool isJson)
		{
			return value switch
			{
				null => "\u2400",
				string s when isJson => CanonicalJson(s),
				string s => s,
				DateTime d => (d.Ticks - (d.Ticks % 10)).ToString(CultureInfo.InvariantCulture),
				DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				byte[] bytes => Convert.ToHexString(bytes),
				bool b => b ? "1" : "0",
				Enum e => e.ToString(),
				IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
				_ => value.ToString() ?? string.Empty
			};
		}

		// Sorted keys, no whitespace: jsonb reorders keys and drops formatting
		private static string CanonicalJson(string json)
		{
			JsonNode? node = JsonNode.Parse(json);
			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream))
			{
				Write(writer, node);
			}

			return Encoding.UTF8.GetString(stream.ToArray());
		}

		private static void Write(Utf8JsonWriter writer, JsonNode? node)
		{
			switch (node)
			{
				case null:
					writer.WriteNullValue();
					break;
				case JsonObject obj:
					writer.WriteStartObject();
					foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
					{
						writer.WritePropertyName(pair.Key);
						Write(writer, pair.Value);
					}
					writer.WriteEndObject();
					break;
				case JsonArray array:
					writer.WriteStartArray();
					foreach (JsonNode? item in array)
					{
						Write(writer, item);
					}
					writer.WriteEndArray();
					break;
				default:
					node.WriteTo(writer);
					break;
			}
		}
	}
}
