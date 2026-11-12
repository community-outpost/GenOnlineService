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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MySqlConnector;
using Npgsql;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Database
{
	// Marks a migration whose raw SQL only adds schema, so the import can read a source that has not applied it
	[AttributeUsage(AttributeTargets.Class, Inherited = false)]
	public sealed class ImportSafeAttribute : Attribute
	{
		public ImportSafeAttribute(string reason)
		{
			Reason = reason;
		}

		public string Reason { get; }
	}

	// An error with a message meant for the operator, printed without a stack trace
	public sealed class ImportException : Exception
	{
		public ImportException(string message) : base(message)
		{
		}
	}

	// The "Import" config section: the database the data is moved from
	public sealed record ImportSource(string Provider, string Host, ushort Port, string Name, string Username, string Password, MySqlSslMode SslMode);

	public sealed class ImportOptions
	{
		public bool DryRun { get; init; }
		public bool Restart { get; init; }

		// (prompt, hint printed when input is not interactive) -> proceed
		public Func<string, string, bool> Confirm { get; init; } = (_, _) => false;
	}

	// "GenOnlineService import": moves a MariaDB/MySQL database into PostgreSQL. Every run does the same steps, see README.md.
	// The source is only ever read. Values go through the value converters of both EF models, so the type rules
	// (unsigned integers, tinyint flags, the superweapons int, jsonb) stay in one place, the models.
	public static class Import
	{
		private const int BatchSize = 2000;
		private const int MaxReportedIssues = 20;
		private const string StateTable = "__import_state";

		// The only table copied incrementally by key. Rows are only ever inserted (Database.AntiCheat.cs: INSERT ... ON
		// DUPLICATE KEY UPDATE report_id = report_id is the only write), so rows already copied cannot go stale.
		// match_history is not here: its rows are updated after the match finished (CommitPlayerOutcome and
		// AttachMatchHistoryMetadata target any of a session's last 50 matches, with no age bound), so it is reloaded.
		private const string AppendOnlyTable = "ac_reviews_modules";

		// Sets every identity / serial sequence to max(column) + 1
		private const string SequencesSql = @"
DO $$
DECLARE
  r record;
  max_id bigint;
  seq text;
BEGIN
  FOR r IN
    SELECT n.nspname AS schema_name,
           c.relname AS table_name,
           a.attname AS column_name,
           a.attidentity <> '' AS is_identity,
           pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), a.attname) AS serial_seq
    FROM pg_attribute a
    JOIN pg_class c ON c.oid = a.attrelid AND c.relkind IN ('r', 'p')
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND a.attnum > 0
      AND NOT a.attisdropped
      AND (a.attidentity <> '' OR pg_get_serial_sequence(format('%I.%I', n.nspname, c.relname), a.attname) IS NOT NULL)
    ORDER BY c.relname, a.attnum
  LOOP
    EXECUTE format('SELECT COALESCE(MAX(%I), 0) FROM %I.%I', r.column_name, r.schema_name, r.table_name) INTO max_id;

    IF r.is_identity THEN
      EXECUTE format('ALTER TABLE %I.%I ALTER COLUMN %I RESTART WITH %s', r.schema_name, r.table_name, r.column_name, max_id + 1);
    ELSE
      seq := r.serial_seq;
      PERFORM setval(seq, GREATEST(max_id, 1), max_id > 0);
    END IF;
  END LOOP;
END
$$;";

		private sealed class Column
		{
			public string Name = "";
			public int Ordinal;
			public string PgType = "";          // type name for the binary COPY, without a length
			public bool PgNullable;
			public bool HasTargetDefault;
			public int MaxLength;               // characters, 0 = none
			public Type ModelClr = typeof(object);
			public Type SourceProviderClr = typeof(object);
			public Type TargetProviderClr = typeof(object);
			public ValueConverter? SourceConverter;
			public ValueConverter? TargetConverter;
			public bool IsDateTime;
			public bool IsJson;
			public bool IsString;
		}

		private sealed class Table
		{
			public string Name = "";
			public List<Column> Columns = new();
			public List<Column> Keys = new();
			public string SelectList = "";
			public string OrderBy = "";
			public string CopySql = "";
		}

		private sealed class State
		{
			public string Table = "";
			public string[]? LastKey;
			public long RowsCopied;
			public string Status = "pending";
			public string Phase = "";
			public string SourceMigration = "";
			public string SourceHost = "";
			public string SourceName = "";
		}

		private readonly record struct Issue(string Table, string Key, string Column, string Problem);

		private sealed class Batch
		{
			public List<object?[]> Rows = new();
			public string[]? LastKey;
			public int Issues;
			public List<Issue> IssueList = new();
		}

		private sealed class Run
		{
			public MySqlConnection Source = null!;
			public NpgsqlConnection Target = null!;
			public Dictionary<string, State> States = new();
			public ImportSource Config = null!;
			public string SourceMigration = "";
		}

		public static ImportSource ReadSource(IConfiguration config, bool targetIsPostgres)
		{
			IConfiguration section = config.GetSection("Import");
			string? provider = section.GetValue<string>("db_provider");

			if (string.IsNullOrWhiteSpace(provider))
			{
				throw new ImportException("Import:db_provider is not set. Configure the Import section (the database to move the data from) first, see README.md.");
			}

			string normalized = provider.Trim().ToLowerInvariant();

			if (!targetIsPostgres || (normalized != "mysql" && normalized != "mariadb"))
			{
				throw new ImportException($"Unsupported import from \"{provider}\" to \"{(targetIsPostgres ? "postgres" : "mysql")}\". Only mysql/mariadb to postgres is supported: Import:db_provider must be \"mysql\" or \"mariadb\" and Database:db_provider must be \"postgres\".");
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

			return new ImportSource(
				normalized,
				Require("db_host"),
				section.GetValue<ushort?>("db_port") ?? 3306,
				Require("db_name"),
				Require("db_username"),
				Require("db_password"),
				sslMode);
		}

		// One small query for the startup check: an import that was started and not completed
		public static async Task<bool> IsUnfinished(DbContext db)
		{
			return await db.Database
				.SqlQueryRaw<bool>($"SELECT to_regclass('{StateTable}') IS NOT NULL AS \"Value\"")
				.SingleAsync();
		}

		private static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

		private static string QM(string identifier) => "`" + identifier.Replace("`", "``") + "`";

		// ---------------------------------------------------------------- model

		// Walks both EF models (not a hand-kept list), so a new entity is picked up and a difference fails loudly
		private static List<Table> BuildPlan()
		{
			using var mySqlDb = new MySqlAppDbContextFactory().CreateDbContext(Array.Empty<string>());
			using var pgDb = new PostgresAppDbContextFactory().CreateDbContext(Array.Empty<string>());

			IRelationalModel mySqlModel = mySqlDb.GetService<IDesignTimeModel>().Model.GetRelationalModel();
			IRelationalModel pgModel = pgDb.GetService<IDesignTimeModel>().Model.GetRelationalModel();

			var mySqlNames = mySqlModel.Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
			var pgNames = pgModel.Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

			if (!mySqlNames.SequenceEqual(pgNames))
			{
				throw new ImportException($"The MySQL and PostgreSQL models have different tables (MySQL only: {string.Join(", ", mySqlNames.Except(pgNames))}; PostgreSQL only: {string.Join(", ", pgNames.Except(mySqlNames))}).");
			}

			var tables = new List<Table>();

			foreach (ITable pgTable in pgModel.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
			{
				ITable mySqlTable = mySqlModel.Tables.First(t => t.Name == pgTable.Name);
				var table = new Table { Name = pgTable.Name };

				var pgColumnNames = pgTable.Columns.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
				var mySqlColumnNames = mySqlTable.Columns.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

				if (!pgColumnNames.SequenceEqual(mySqlColumnNames))
				{
					throw new ImportException($"Table {pgTable.Name} has different columns in the models (MySQL only: {string.Join(", ", mySqlColumnNames.Except(pgColumnNames))}; PostgreSQL only: {string.Join(", ", pgColumnNames.Except(mySqlColumnNames))}).");
				}

				foreach (IColumn pgColumn in pgTable.Columns.OrderBy(c => c.Name, StringComparer.Ordinal))
				{
					IColumn mySqlColumn = mySqlTable.Columns.First(c => c.Name == pgColumn.Name);
					IColumnMapping pgMapping = pgColumn.PropertyMappings[0];
					IColumnMapping mySqlMapping = mySqlColumn.PropertyMappings[0];

					if (pgMapping.Property.ClrType != mySqlMapping.Property.ClrType)
					{
						throw new ImportException($"Column {pgTable.Name}.{pgColumn.Name} has type {mySqlMapping.Property.ClrType.Name} in the MySQL model and {pgMapping.Property.ClrType.Name} in the PostgreSQL model.");
					}

					Type modelClr = Nullable.GetUnderlyingType(pgMapping.Property.ClrType) ?? pgMapping.Property.ClrType;
					string storeType = pgColumn.StoreType;
					int paren = storeType.IndexOf('(');

					var column = new Column
					{
						Name = pgColumn.Name,
						Ordinal = table.Columns.Count,
						PgType = (paren >= 0 ? storeType.Substring(0, paren) : storeType).Trim(),
						PgNullable = pgColumn.IsNullable,
						HasTargetDefault = pgColumn.DefaultValue != null || pgColumn.DefaultValueSql != null || pgColumn.ComputedColumnSql != null,
						MaxLength = pgMapping.Property.GetMaxLength() ?? 0,
						ModelClr = modelClr,
						SourceConverter = mySqlMapping.TypeMapping.Converter,
						TargetConverter = pgMapping.TypeMapping.Converter,
						SourceProviderClr = Unwrap(mySqlMapping.TypeMapping.Converter?.ProviderClrType ?? mySqlMapping.TypeMapping.ClrType),
						TargetProviderClr = Unwrap(pgMapping.TypeMapping.Converter?.ProviderClrType ?? pgMapping.TypeMapping.ClrType),
						IsDateTime = modelClr == typeof(DateTime),
						IsString = Unwrap(pgMapping.TypeMapping.Converter?.ProviderClrType ?? pgMapping.TypeMapping.ClrType) == typeof(string)
					};

					column.IsJson = column.PgType == "jsonb" || column.PgType == "json";
					table.Columns.Add(column);
				}

				var pgKey = pgTable.PrimaryKey?.Columns.Select(c => c.Name).ToList()
					?? throw new ImportException($"Table {pgTable.Name} has no primary key in the PostgreSQL model.");
				var mySqlKey = mySqlTable.PrimaryKey?.Columns.Select(c => c.Name).ToList();

				if (mySqlKey == null || !pgKey.SequenceEqual(mySqlKey))
				{
					throw new ImportException($"Table {pgTable.Name} has a different primary key in the two models.");
				}

				table.Keys = pgKey.Select(k => table.Columns.First(c => c.Name == k)).ToList();
				FinishTable(table);
				tables.Add(table);
			}

			Table appendOnly = tables.FirstOrDefault(t => t.Name == AppendOnlyTable)
				?? throw new ImportException($"Table {AppendOnlyTable} is not in the model.");

			if (appendOnly.Keys.Count != 1 || (appendOnly.Keys[0].SourceProviderClr != typeof(ulong) && appendOnly.Keys[0].SourceProviderClr != typeof(long)))
			{
				throw new ImportException($"Table {AppendOnlyTable} must have a single integer key.");
			}

			return tables;
		}

		private static void FinishTable(Table table)
		{
			for (int i = 0; i < table.Columns.Count; i++)
			{
				table.Columns[i].Ordinal = i;
			}

			table.SelectList = string.Join(", ", table.Columns.Select(c => QM(c.Name)));
			table.OrderBy = string.Join(", ", table.Keys.Select(k => QM(k.Name)));
			table.CopySql = $"COPY {Q(table.Name)} ({string.Join(", ", table.Columns.Select(c => Q(c.Name)))}) FROM STDIN (FORMAT BINARY)";
		}

		// An older source lacks tables and columns that later migrations add, so only what both sides have is copied
		private static List<Table> NarrowToSource(List<Table> modelTables, Dictionary<string, HashSet<string>> sourceColumns, List<string> warnings)
		{
			var tables = new List<Table>();

			foreach (Table model in modelTables)
			{
				if (!sourceColumns.TryGetValue(model.Name, out HashSet<string>? present))
				{
					warnings.Add($"table {model.Name} is not in the source database, it stays empty");
					continue;
				}

				var absent = model.Columns.Where(c => !present.Contains(c.Name)).ToList();

				foreach (Column c in absent)
				{
					if (model.Keys.Contains(c))
					{
						throw new ImportException($"The source table {model.Name} has no key column {c.Name}. Run \"GenOnlineService migrate --yes\" on the MariaDB host with db_provider set to mysql, then run the import again.");
					}

					if (!c.PgNullable && !c.HasTargetDefault)
					{
						throw new ImportException($"The source table {model.Name} has no column {c.Name}, which PostgreSQL requires and has no default for. Run \"GenOnlineService migrate --yes\" on the MariaDB host with db_provider set to mysql, then run the import again.");
					}

					warnings.Add($"column {model.Name}.{c.Name} is not in the source database, it gets its default");
				}

				foreach (string extra in present.Where(n => !model.Columns.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).OrderBy(n => n, StringComparer.Ordinal))
				{
					warnings.Add($"column {model.Name}.{extra} is only in the source database, it is not imported");
				}

				var table = new Table { Name = model.Name, Keys = model.Keys, Columns = model.Columns.Except(absent).ToList() };
				FinishTable(table);
				tables.Add(table);
			}

			foreach (string extra in sourceColumns.Keys.Where(n => !n.StartsWith("__", StringComparison.Ordinal) && !modelTables.Any(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase))).OrderBy(n => n, StringComparer.Ordinal))
			{
				warnings.Add($"table {extra} is only in the source database, it is not imported");
			}

			return tables;
		}

		// Two runs copy the same thing only when they read the same tables and columns
		private static string Shape(List<Table> tables)
		{
			string text = string.Join(";", tables.Select(t => t.Name + ":" + string.Join(",", t.Columns.Select(c => c.Name))));
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
		}

		private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

		// ---------------------------------------------------------------- values

		private static object Coerce(object value, Type type)
		{
			if (type.IsInstanceOfType(value))
			{
				return value;
			}

			if (type.IsEnum)
			{
				return Enum.ToObject(type, value);
			}

			return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
		}

		// Source value -> value for the PostgreSQL column. A problem is a value PostgreSQL cannot store, it is never altered.
		private static object? ConvertValue(Column c, object? raw, out string? problem)
		{
			problem = null;

			if (raw is DBNull)
			{
				raw = null;
			}

			// The source connection has ConvertZeroDateTime, so a MariaDB zero date ('0000-00-00') arrives as DateTime.MinValue.
			// PostgreSQL has no zero date: it becomes NULL where the column allows it and the Unix epoch where it does not.
			// (A real 0001-01-01 00:00:00 cannot be told apart from it, and does not occur in this service's data.)
			if (c.IsDateTime && raw is DateTime zero && zero == DateTime.MinValue)
			{
				if (c.PgNullable)
				{
					return null;
				}

				raw = DateTime.UnixEpoch;
			}

			if (raw == null)
			{
				if (!c.PgNullable)
				{
					problem = "is NULL but the PostgreSQL column is NOT NULL";
				}

				return null;
			}

			object provider = Coerce(raw, c.SourceProviderClr);
			object model = c.SourceConverter != null ? c.SourceConverter.ConvertFromProvider(provider)! : provider;

			// The unsigned to signed conversion would wrap silently
			if (model is ulong unsigned && unsigned > long.MaxValue && c.TargetProviderClr == typeof(long))
			{
				problem = "does not fit a signed 64-bit integer";
				return null;
			}

			object target = c.TargetConverter != null ? c.TargetConverter.ConvertToProvider(model)! : model;
			target = Coerce(target, c.TargetProviderClr);

			if (target is string text)
			{
				problem = CheckText(c, text);
			}

			return target;
		}

		private static string? CheckText(Column c, string text)
		{
			if (text.Contains('\0'))
			{
				return "contains a NUL character, which PostgreSQL cannot store";
			}

			if (c.IsJson)
			{
				try
				{
					var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { MaxDepth = 1000 });

					while (reader.Read())
					{
						if ((reader.TokenType == JsonTokenType.String || reader.TokenType == JsonTokenType.PropertyName) && reader.GetString()!.Contains('\0'))
						{
							return "contains \\u0000 in JSON, which jsonb cannot store";
						}
					}
				}
				catch (JsonException)
				{
					return "is not valid JSON";
				}
			}

			if (c.MaxLength > 0 && text.Length > c.MaxLength && text.EnumerateRunes().Count() > c.MaxLength)
			{
				return $"is longer than {c.MaxLength} characters";
			}

			return null;
		}

		private static string FormatKey(Table t, object?[] raw)
		{
			return string.Join(", ", t.Keys.Select(k =>
			{
				string value = Convert.ToString(raw[k.Ordinal], CultureInfo.InvariantCulture) ?? "NULL";
				return $"{k.Name}={(value.Length > 40 ? value.Substring(0, 40) + "..." : value)}";
			}));
		}

		// ---------------------------------------------------------------- source

		private static async Task<MySqlConnection> OpenSource(ImportSource source)
		{
			var csb = new MySqlConnectionStringBuilder
			{
				Server = source.Host,
				Port = source.Port,
				Database = source.Name,
				UserID = source.Username,
				Password = source.Password,
				SslMode = source.SslMode,
				// A pooled connection is reset between uses, which would drop the session settings below
				Pooling = false,
				// The source may be on a slow link: generous connect and command timeouts, and TCP keepalives so a
				// dead link fails instead of hanging. A failed run leaves a resumable state (every batch is one transaction).
				ConnectionTimeout = 30,
				DefaultCommandTimeout = 600,
				Keepalive = 30,
				ConvertZeroDateTime = true,
				// the EF provider behind the legacy schema check requires both on the connection it borrows
				AllowUserVariables = true,
				UseAffectedRows = false
			};

			var connection = new MySqlConnection(csb.ConnectionString);
			await connection.OpenAsync();

			try
			{
				// The import never writes to the source: the server rejects any write in this session
				await ExecSource(connection, "SET SESSION TRANSACTION READ ONLY");

				// TIMESTAMP columns are returned in the session time zone, the service treats every date as UTC
				await ExecSource(connection, "SET SESSION time_zone = '+00:00'");

				object? readOnly;

				try
				{
					readOnly = await ScalarSource(connection, "SELECT @@session.transaction_read_only");
				}
				catch (MySqlException)
				{
					readOnly = await ScalarSource(connection, "SELECT @@session.tx_read_only");
				}

				if (Convert.ToInt32(readOnly, CultureInfo.InvariantCulture) != 1)
				{
					throw new ImportException("The source session could not be set to read only, refusing to continue.");
				}
			}
			catch
			{
				await connection.DisposeAsync();
				throw;
			}

			return connection;
		}

		private static async Task ExecSource(MySqlConnection connection, string sql)
		{
			await using var command = new MySqlCommand(sql, connection);
			await command.ExecuteNonQueryAsync();
		}

		private static async Task<object?> ScalarSource(MySqlConnection connection, string sql, params MySqlParameter[] parameters)
		{
			await using var command = new MySqlCommand(sql, connection);
			command.Parameters.AddRange(parameters);
			object? value = await command.ExecuteScalarAsync();
			return value is DBNull ? null : value;
		}

		// Null when a source without this migration can still be imported, otherwise why it cannot
		private static Dictionary<string, string?> ClassifyMigrations(MySqlAppDbContext context)
		{
			var migrations = context.GetService<IMigrationsAssembly>();
			var result = new Dictionary<string, string?>(StringComparer.Ordinal);

			foreach (var (id, type) in migrations.Migrations)
			{
				Migration migration = migrations.CreateMigration(type, context.Database.ProviderName!);
				result[id] = type.GetCustomAttribute<ImportSafeAttribute>() != null ? null : UnsafeReason(migration.UpOperations);
			}

			return result;
		}

		// Renames, type changes and raw SQL can change what existing data means, so the import never guesses across them
		private static string? UnsafeReason(IReadOnlyList<MigrationOperation> operations)
		{
			foreach (MigrationOperation operation in operations)
			{
				switch (operation)
				{
					case CreateTableOperation or DropTableOperation or DropColumnOperation or CreateIndexOperation or DropIndexOperation or DropForeignKeyOperation:
						continue;
					case AddColumnOperation add when add.IsNullable || add.DefaultValue != null || add.DefaultValueSql != null:
						continue;
					case AddColumnOperation add:
						return $"it adds column {add.Table}.{add.Name} as NOT NULL without a default";
					default:
						return $"it contains a step of type {operation.GetType().Name.Replace("Operation", "")}, which can change existing data";
				}
			}

			return null;
		}

		// A legacy install without migration history passes the same read only check as --baseline before it is imported as the first migration
		private static async Task<string> CheckLegacySource(MySqlConnection source)
		{
			var options = new DbContextOptionsBuilder<MySqlAppDbContext>().UseMySql(source, MySqlAppDbContext.DesignTimeServerVersion).Options;
			await using var db = new MySqlAppDbContext(options);
			BaselinePlan plan = await MigrationBaseline.Plan(db);

			if (plan.Error != null)
			{
				throw new ImportException($"The source database has no migration history and the import cannot tell its version. The --baseline check reports: {plan.Error.Replace("then run --baseline again", "then run the import again")}");
			}

			return plan.MigrationId;
		}

		private static async Task<Dictionary<string, HashSet<string>>> ReadSourceColumns(MySqlConnection source)
		{
			var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
			const string sql = "SELECT c.table_name, c.column_name FROM information_schema.columns c JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name WHERE c.table_schema = DATABASE() AND t.table_type = 'BASE TABLE'";

			await using var command = new MySqlCommand(sql, source);
			await using var reader = await command.ExecuteReaderAsync();

			while (await reader.ReadAsync())
			{
				string table = reader.GetString(0);

				if (!result.TryGetValue(table, out HashSet<string>? columns))
				{
					columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					result[table] = columns;
				}

				columns.Add(reader.GetString(1));
			}

			return result;
		}

		private static async Task<List<string>> ReadSourceMigrations(MySqlConnection source)
		{
			object? exists = await ScalarSource(source, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = '__EFMigrationsHistory'");

			if (Convert.ToInt64(exists, CultureInfo.InvariantCulture) == 0)
			{
				return new List<string>();
			}

			var ids = new List<string>();
			await using var command = new MySqlCommand("SELECT `MigrationId` FROM `__EFMigrationsHistory` ORDER BY `MigrationId`", source);
			await using var reader = await command.ExecuteReaderAsync();

			while (await reader.ReadAsync())
			{
				ids.Add(reader.GetString(0));
			}

			return ids;
		}

		// WHERE for the rows after a key, written out as ORs so it works with any key and uses the primary key index
		private static string KeysetWhere(Table t, string[] last, List<MySqlParameter> parameters)
		{
			for (int i = 0; i < t.Keys.Count; i++)
			{
				parameters.Add(new MySqlParameter("@k" + i, Coerce(last[i], t.Keys[i].SourceProviderClr)));
			}

			var branches = new List<string>();

			for (int i = 0; i < t.Keys.Count; i++)
			{
				var parts = new List<string>();

				for (int j = 0; j < i; j++)
				{
					parts.Add($"{QM(t.Keys[j].Name)} = @k{j}");
				}

				parts.Add($"{QM(t.Keys[i].Name)} > @k{i}");
				branches.Add("(" + string.Join(" AND ", parts) + ")");
			}

			return string.Join(" OR ", branches);
		}

		private static async Task<Batch> ReadRows(MySqlConnection source, Table t, string? where, List<MySqlParameter> parameters, int? limit)
		{
			var sql = new StringBuilder($"SELECT {t.SelectList} FROM {QM(t.Name)}");

			if (where != null)
			{
				sql.Append(" WHERE ").Append(where);
			}

			sql.Append(" ORDER BY ").Append(t.OrderBy);

			if (limit != null)
			{
				sql.Append(" LIMIT ").Append(limit.Value);
			}

			var batch = new Batch();
			await using var command = new MySqlCommand(sql.ToString(), source);
			command.Parameters.AddRange(parameters.ToArray());
			await using var reader = await command.ExecuteReaderAsync();

			if (reader.FieldCount != t.Columns.Count)
			{
				throw new ImportException($"Table {t.Name} returned {reader.FieldCount} columns, the model has {t.Columns.Count}.");
			}

			while (await reader.ReadAsync())
			{
				var raw = new object?[t.Columns.Count];
				reader.GetValues(raw!);

				var row = new object?[raw.Length];

				for (int i = 0; i < raw.Length; i++)
				{
					Column column = t.Columns[i];
					row[i] = ConvertValue(column, raw[i], out string? problem);

					if (problem != null)
					{
						batch.Issues++;

						if (batch.IssueList.Count < MaxReportedIssues)
						{
							batch.IssueList.Add(new Issue(t.Name, FormatKey(t, raw), column.Name, problem));
						}
					}
				}

				batch.Rows.Add(row);
				batch.LastKey = t.Keys.Select(k => Convert.ToString(raw[k.Ordinal], CultureInfo.InvariantCulture) ?? "").ToArray();
			}

			return batch;
		}

		private static string DescribeIssues(IEnumerable<Issue> issues)
		{
			return string.Join(Environment.NewLine, issues.Select(i => $"  {i.Table} ({i.Key}): column {i.Column} {i.Problem}"));
		}

		private static void ThrowIfIssues(Batch batch)
		{
			if (batch.Issues > 0)
			{
				throw new ImportException($"{batch.Issues} value(s) cannot be stored in PostgreSQL. Fix them in the source (nothing is changed by the import) and run it again:{Environment.NewLine}{DescribeIssues(batch.IssueList)}");
			}
		}

		// ---------------------------------------------------------------- target

		private static async Task ExecTarget(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
		{
			await using var command = new NpgsqlCommand(sql, connection);

			foreach (var (name, value) in parameters)
			{
				command.Parameters.AddWithValue(name, value ?? DBNull.Value);
			}

			await command.ExecuteNonQueryAsync();
		}

		private static async Task<object?> ScalarTarget(NpgsqlConnection connection, string sql)
		{
			await using var command = new NpgsqlCommand(sql, connection);
			object? value = await command.ExecuteScalarAsync();
			return value is DBNull ? null : value;
		}

		private static async Task<Dictionary<string, State>?> LoadStates(NpgsqlConnection target)
		{
			if (!(bool)(await ScalarTarget(target, $"SELECT to_regclass('{StateTable}') IS NOT NULL"))!)
			{
				return null;
			}

			var states = new Dictionary<string, State>();
			await using var command = new NpgsqlCommand($"SELECT table_name, last_key, rows_copied, status, source_migration, phase, source_host, source_name FROM {StateTable}", target);
			await using var reader = await command.ExecuteReaderAsync();

			while (await reader.ReadAsync())
			{
				var state = new State
				{
					Table = reader.GetString(0),
					LastKey = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<string[]>(reader.GetString(1)),
					RowsCopied = reader.GetInt64(2),
					Status = reader.GetString(3),
					SourceMigration = reader.GetString(4),
					Phase = reader.GetString(5),
					SourceHost = reader.GetString(6),
					SourceName = reader.GetString(7)
				};

				states[state.Table] = state;
			}

			return states;
		}

		private static async Task SaveState(NpgsqlConnection target, State state)
		{
			await ExecTarget(target, $@"
				INSERT INTO {StateTable} (table_name, last_key, rows_copied, status, source_migration, phase, source_host, source_name, updated_at)
				VALUES (@table, @last, @rows, @status, @migration, @phase, @host, @name, now())
				ON CONFLICT (table_name) DO UPDATE SET last_key = EXCLUDED.last_key, rows_copied = EXCLUDED.rows_copied,
					status = EXCLUDED.status, phase = EXCLUDED.phase, updated_at = now()",
				("table", state.Table),
				("last", state.LastKey == null ? null : JsonSerializer.Serialize(state.LastKey)),
				("rows", state.RowsCopied),
				("status", state.Status),
				("migration", state.SourceMigration),
				("phase", state.Phase),
				("host", state.SourceHost),
				("name", state.SourceName));
		}

		private static async Task<long> CopyRows(NpgsqlConnection target, Table t, List<object?[]> rows)
		{
			await using var importer = await target.BeginBinaryImportAsync(t.CopySql);

			foreach (object?[] row in rows)
			{
				await importer.StartRowAsync();

				for (int i = 0; i < row.Length; i++)
				{
					if (row[i] == null)
					{
						await importer.WriteNullAsync();
					}
					else
					{
						await importer.WriteAsync(row[i]!, t.Columns[i].PgType);
					}
				}
			}

			return (long)await importer.CompleteAsync();
		}

		private static async Task<bool> HasRows(NpgsqlConnection target, Table t)
		{
			return (bool)(await ScalarTarget(target, $"SELECT EXISTS (SELECT 1 FROM {Q(t.Name)})"))!;
		}

		private static string TruncateAllSql(List<Table> tables) => "TRUNCATE TABLE " + string.Join(", ", tables.Select(t => Q(t.Name)));

		private static string Elapsed(Stopwatch watch) => $"{watch.Elapsed.TotalSeconds:F1}s";

		// ---------------------------------------------------------------- run

		public static async Task<int> Execute(AppDbContext targetDb, string targetConnectionString, ImportSource source, ImportOptions options)
		{
			List<Table> modelTables = BuildPlan();
			Console.WriteLine($"Model: {modelTables.Count} tables.");

			var targetCsb = new NpgsqlConnectionStringBuilder(targetConnectionString)
			{
				Pooling = false,
				MinPoolSize = 0,
				CommandTimeout = 0
			};

			// Target: PostgreSQL with every migration applied
			var appliedTarget = (await targetDb.Database.GetAppliedMigrationsAsync()).ToList();
			var pendingTarget = (await targetDb.Database.GetPendingMigrationsAsync()).ToList();

			if (appliedTarget.Count == 0 || pendingTarget.Count > 0)
			{
				throw new ImportException($"The target PostgreSQL database does not have all migrations applied ({pendingTarget.Count} pending). Run \"GenOnlineService migrate --yes\" against it first.");
			}

			await using MySqlConnection sourceConnection = await OpenSource(source);
			await using var targetConnection = new NpgsqlConnection(targetCsb.ConnectionString);
			await targetConnection.OpenAsync();

			// Source: MariaDB at this version or an older one the import can bridge
			using MySqlAppDbContext sourceModel = new MySqlAppDbContextFactory().CreateDbContext(Array.Empty<string>());
			var expected = sourceModel.Database.GetMigrations().OrderBy(m => m, StringComparer.Ordinal).ToList();
			var unsafeReasons = ClassifyMigrations(sourceModel);
			var appliedSource = await ReadSourceMigrations(sourceConnection);

			if (appliedSource.Count == 0)
			{
				string first = await CheckLegacySource(sourceConnection);
				Console.WriteLine($"The source database has no migration history and matches migration {first}, it is imported as that version.");
				appliedSource.Add(first);
			}

			var unknown = appliedSource.Except(expected).ToList();

			if (unknown.Count > 0)
			{
				throw new ImportException($"The source database has migration(s) this version does not know ({string.Join(", ", unknown)}). Use the same or a newer version of the service for the import.");
			}

			var missing = expected.Except(appliedSource).ToList();
			var blocking = missing.Where(id => unsafeReasons[id] != null).Select(id => $"{id} ({unsafeReasons[id]})").ToList();

			if (blocking.Count > 0)
			{
				throw new ImportException($"The source database is missing migration(s) the import cannot bridge: {string.Join(", ", blocking)}. On the MariaDB host run \"GenOnlineService migrate --yes\" with db_provider set to mysql, then run the import again.");
			}

			foreach (string id in missing)
			{
				Console.WriteLine($"Note: the source database has not applied migration {id}, the import bridges it.");
			}

			var warnings = new List<string>();
			List<Table> tables = NarrowToSource(modelTables, await ReadSourceColumns(sourceConnection), warnings);

			foreach (string warning in warnings)
			{
				Console.WriteLine($"Warning: {warning}.");
			}

			// a migration that changes data or the copied columns restarts an import in progress, any other migration does not
			string lastApplied = appliedSource.Max(StringComparer.Ordinal)!;
			string lastDataMigration = appliedSource.Where(id => unsafeReasons[id] != null).Max(StringComparer.Ordinal) ?? "none";
			string sourceMigration = $"{lastDataMigration}, columns {Shape(tables)}";
			object? version = await ScalarSource(sourceConnection, "SELECT VERSION()");

			var run = new Run { Source = sourceConnection, Target = targetConnection, Config = source, SourceMigration = sourceMigration };
			Dictionary<string, State>? existing = await LoadStates(targetConnection);

			// Target guard: rows without a state table were not put there by an import
			if (existing == null)
			{
				foreach (Table t in modelTables)
				{
					if (await HasRows(targetConnection, t))
					{
						throw new ImportException($"The target already has data (table {t.Name}) and no import is in progress. The import only fills an empty database; to repeat a completed import, recreate the PostgreSQL database and run \"GenOnlineService migrate --yes\" again.");
					}
				}
			}
			else
			{
				State any = existing.Values.First();
				bool sameSource = string.Equals(any.SourceHost, source.Host, StringComparison.OrdinalIgnoreCase) && string.Equals(any.SourceName, source.Name, StringComparison.Ordinal);

				if (!sameSource && !options.Restart)
				{
					throw new ImportException($"The import in progress reads from a different source (host {any.SourceHost}, database {any.SourceName}) than this run (host {source.Host}, database {source.Name}). Use the original source, or run with --restart to discard the imported data and start over.");
				}

				if (sameSource && any.SourceMigration != sourceMigration && !options.Restart)
				{
					throw new ImportException($"The source schema changed since the import started (was {any.SourceMigration}, now {sourceMigration}). Run with --restart to start over.");
				}
			}

			bool restart = options.Restart && existing != null;

			var sourceCounts = new Dictionary<string, long>();

			foreach (Table t in tables)
			{
				sourceCounts[t.Name] = Convert.ToInt64(await ScalarSource(sourceConnection, $"SELECT COUNT(*) FROM {QM(t.Name)}"), CultureInfo.InvariantCulture);
			}

			Console.WriteLine($"Source: {source.Provider} {version} at {source.Host}:{source.Port}, database {source.Name} (migration {lastApplied}), opened read only.");
			Console.WriteLine($"Target: postgres at {targetCsb.Host}:{targetCsb.Port}, database {targetCsb.Database}.");
			Console.WriteLine(existing == null ? "No import in progress." : $"Import in progress: {existing.Values.Sum(s => s.RowsCopied)} rows recorded so far.{(restart ? " --restart discards it." : "")}");
			Console.WriteLine("Plan:");

			foreach (Table t in tables)
			{
				string action;

				if (t.Name == AppendOnlyTable)
				{
					State? s = restart ? null : existing?.GetValueOrDefault(t.Name);
					action = s?.LastKey == null ? "copy all rows" : $"copy rows after key {string.Join(",", s.LastKey)}";
				}
				else
				{
					action = "truncate and reload";
				}

				Console.WriteLine($"  {t.Name,-28} {sourceCounts[t.Name],12} rows  {action}");
			}

			Console.WriteLine("Then: reset the sequences, verify row count and min/max key per table; if everything matches, remove the import state and the import is complete.");

			if (options.DryRun)
			{
				return await DryRun(run, tables);
			}

			Console.WriteLine();
			Console.WriteLine("IMPORTANT: the run that completes the import must be done with the service stopped. A service that is still writing during the run can lose those writes without verification noticing.");
			Console.WriteLine("A run with the service live is fine as a preparation: it ends with differences and keeps its progress.");

			if (!options.Confirm(restart ? "Discard the imported data, start over and run the import?" : "Run the import?", "Input is not interactive. Pass --yes to run the import in an unattended run."))
			{
				return 1;
			}

			if (restart)
			{
				await ExecTarget(targetConnection, TruncateAllSql(modelTables));
				await ExecTarget(targetConnection, $"DROP TABLE {StateTable}");
				existing = null;
				Console.WriteLine("Discarded the previous import.");
			}

			if (existing == null)
			{
				await using var tx = await targetConnection.BeginTransactionAsync();
				await ExecTarget(targetConnection, $@"
					CREATE TABLE {StateTable} (
						table_name text PRIMARY KEY,
						last_key text NULL,
						rows_copied bigint NOT NULL DEFAULT 0,
						status text NOT NULL,
						source_migration text NOT NULL,
						phase text NOT NULL,
						source_host text NOT NULL,
						source_name text NOT NULL,
						updated_at timestamptz NOT NULL DEFAULT now())");

				existing = new Dictionary<string, State>();

				foreach (Table t in tables)
				{
					var state = new State { Table = t.Name, SourceMigration = sourceMigration, SourceHost = source.Host, SourceName = source.Name, Phase = "new" };
					existing[t.Name] = state;
					await SaveState(targetConnection, state);
				}

				await tx.CommitAsync();
			}

			run.States = existing;

			foreach (Table t in tables.Where(t => t.Name == AppendOnlyTable))
			{
				await CatchUp(run, t);
			}

			foreach (Table t in tables.Where(t => t.Name != AppendOnlyTable))
			{
				await Reload(run, t);
			}

			await ExecTarget(targetConnection, SequencesSql);
			Console.WriteLine("Sequences reset.");

			List<string> differences = await Verify(run, tables);

			if (differences.Count > 0)
			{
				Console.Error.WriteLine();
				Console.Error.WriteLine("Catch-up run finished, the import is NOT complete yet: the source and the target differ.");

				foreach (string line in differences)
				{
					Console.Error.WriteLine("  " + line);
				}

				Console.Error.WriteLine("This is the normal result of a run while the service is live: the differences are rows written while it copied. It is not an error, the progress is kept (the service cannot start on this database yet).");
				Console.Error.WriteLine("Run the import again to catch up. The run that completes the import must be done with the service stopped.");
				return 1;
			}

			await ExecTarget(targetConnection, $"DROP TABLE {StateTable}");
			Console.WriteLine("Verified: row counts and min/max keys match for all tables. Import complete, the service can start on PostgreSQL (set db_provider to postgres).");
			return 0;
		}

		// Reads everything and writes nothing, including no state table
		private static async Task<int> DryRun(Run run, List<Table> tables)
		{
			Console.WriteLine();
			Console.WriteLine("Dry run: scanning every table for values PostgreSQL cannot store. Nothing is written.");

			int total = 0;

			foreach (Table t in tables)
			{
				var watch = Stopwatch.StartNew();
				string[]? last = null;
				int issues = 0;
				var reported = new List<Issue>();
				long rows = 0;

				while (true)
				{
					var parameters = new List<MySqlParameter>();
					Batch batch = await ReadRows(run.Source, t, last == null ? null : KeysetWhere(t, last, parameters), parameters, BatchSize);

					if (batch.Rows.Count == 0)
					{
						break;
					}

					rows += batch.Rows.Count;
					issues += batch.Issues;
					reported.AddRange(batch.IssueList.Take(MaxReportedIssues - reported.Count));
					last = batch.LastKey;

					if (batch.Rows.Count < BatchSize)
					{
						break;
					}
				}

				total += issues;
				Console.WriteLine($"  {t.Name}: {rows} rows scanned in {Elapsed(watch)}, {issues} unstorable value(s)");

				if (issues > 0)
				{
					Console.WriteLine(DescribeIssues(reported));
				}
			}

			if (total > 0)
			{
				Console.Error.WriteLine($"{total} value(s) cannot be stored in PostgreSQL and would stop the import. Fix them in the source, the import does not change data.");
				return 1;
			}

			Console.WriteLine("Dry run finished: no unstorable values.");
			return 0;
		}

		private static async Task CatchUp(Run run, Table t)
		{
			var watch = Stopwatch.StartNew();
			State state = run.States[t.Name];
			long copied = 0;

			while (true)
			{
				var parameters = new List<MySqlParameter>();
				Batch batch = await ReadRows(run.Source, t, state.LastKey == null ? null : KeysetWhere(t, state.LastKey, parameters), parameters, BatchSize);
				ThrowIfIssues(batch);

				if (batch.Rows.Count == 0)
				{
					break;
				}

				// The rows and the state move together, a crash cannot lose or repeat a batch
				await using var tx = await run.Target.BeginTransactionAsync();
				await CopyRows(run.Target, t, batch.Rows);
				state.LastKey = batch.LastKey;
				state.RowsCopied += batch.Rows.Count;
				state.Status = "copying";
				state.Phase = "catch-up";
				await SaveState(run.Target, state);
				await tx.CommitAsync();

				copied += batch.Rows.Count;

				if (batch.Rows.Count < BatchSize)
				{
					break;
				}
			}

			copied += await RepairGaps(run, t, state);

			state.Status = "copied";
			state.Phase = "catch-up";
			await SaveState(run.Target, state);
			Console.WriteLine($"  {t.Name}: {copied} new rows in {Elapsed(watch)}");
		}

		// A row can commit after a row with a higher key was read (auto increment keys are assigned before commit), and
		// would then be skipped for good. Compare the counts up to the last key and copy what is missing.
		private static async Task<long> RepairGaps(Run run, Table t, State state)
		{
			if (state.LastKey == null)
			{
				return 0;
			}

			Column key = t.Keys[0];
			object? sourceCount = await ScalarSource(run.Source, $"SELECT COUNT(*) FROM {QM(t.Name)} WHERE {QM(key.Name)} <= @last",
				new MySqlParameter("@last", Coerce(state.LastKey[0], key.SourceProviderClr)));
			long targetCount = Convert.ToInt64(await ScalarTarget(run.Target, $"SELECT COUNT(*) FROM {Q(t.Name)}"), CultureInfo.InvariantCulture);

			if (Convert.ToInt64(sourceCount, CultureInfo.InvariantCulture) == targetCount)
			{
				return 0;
			}

			var present = new HashSet<long>();
			await using (var command = new NpgsqlCommand($"SELECT {Q(key.Name)} FROM {Q(t.Name)}", run.Target))
			await using (var reader = await command.ExecuteReaderAsync())
			{
				while (await reader.ReadAsync())
				{
					present.Add(reader.GetInt64(0));
				}
			}

			var missing = new List<long>();
			await using (var command = new MySqlCommand($"SELECT {QM(key.Name)} FROM {QM(t.Name)} WHERE {QM(key.Name)} <= @last ORDER BY {QM(key.Name)}", run.Source))
			{
				command.Parameters.AddWithValue("@last", Coerce(state.LastKey[0], key.SourceProviderClr));
				await using var reader = await command.ExecuteReaderAsync();

				while (await reader.ReadAsync())
				{
					long id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);

					if (!present.Contains(id))
					{
						missing.Add(id);
					}
				}
			}

			long added = 0;

			foreach (long[] chunk in missing.Chunk(BatchSize))
			{
				var parameters = chunk.Select((id, i) => new MySqlParameter("@m" + i, Coerce(id, key.SourceProviderClr))).ToList();
				string where = $"{QM(key.Name)} IN ({string.Join(", ", parameters.Select(p => p.ParameterName))})";
				Batch batch = await ReadRows(run.Source, t, where, parameters, null);
				ThrowIfIssues(batch);

				await using var tx = await run.Target.BeginTransactionAsync();
				await CopyRows(run.Target, t, batch.Rows);
				state.RowsCopied += batch.Rows.Count;
				await SaveState(run.Target, state);
				await tx.CommitAsync();
				added += batch.Rows.Count;
			}

			return added;
		}

		private static async Task Reload(Run run, Table t)
		{
			var watch = Stopwatch.StartNew();
			State state = run.States[t.Name];
			long rows = 0;
			string[]? last = null;

			// The whole table in one transaction: until it commits the previous contents stay
			await using var tx = await run.Target.BeginTransactionAsync();
			await ExecTarget(run.Target, $"TRUNCATE TABLE {Q(t.Name)}");

			while (true)
			{
				var parameters = new List<MySqlParameter>();
				Batch batch = await ReadRows(run.Source, t, last == null ? null : KeysetWhere(t, last, parameters), parameters, BatchSize);
				ThrowIfIssues(batch);

				if (batch.Rows.Count == 0)
				{
					break;
				}

				await CopyRows(run.Target, t, batch.Rows);
				rows += batch.Rows.Count;
				last = batch.LastKey;

				if (batch.Rows.Count < BatchSize)
				{
					break;
				}
			}

			state.LastKey = last;
			state.RowsCopied = rows;
			state.Status = "copied";
			state.Phase = "reload";
			await SaveState(run.Target, state);
			await tx.CommitAsync();

			Console.WriteLine($"  {t.Name}: {rows} rows reloaded in {Elapsed(watch)}");
		}

		// ---------------------------------------------------------------- verify

		private static async Task<List<string>> Verify(Run run, List<Table> tables)
		{
			Console.WriteLine("Verifying:");
			var differences = new List<string>();

			foreach (Table t in tables)
			{
				long sourceCount = Convert.ToInt64(await ScalarSource(run.Source, $"SELECT COUNT(*) FROM {QM(t.Name)}"), CultureInfo.InvariantCulture);
				long targetCount = Convert.ToInt64(await ScalarTarget(run.Target, $"SELECT COUNT(*) FROM {Q(t.Name)}"), CultureInfo.InvariantCulture);
				string sourceMin = await SourceKeyEdge(run.Source, t, false);
				string targetMin = await TargetKeyEdge(run.Target, t, false);
				string sourceMax = await SourceKeyEdge(run.Source, t, true);
				string targetMax = await TargetKeyEdge(run.Target, t, true);

				var problems = new List<string>();

				if (sourceCount != targetCount)
				{
					problems.Add($"{t.Name}: {sourceCount} rows in the source, {targetCount} in the target");
				}

				if (sourceMin != targetMin)
				{
					problems.Add($"{t.Name}: lowest key is {sourceMin} in the source, {targetMin} in the target");
				}

				if (sourceMax != targetMax)
				{
					problems.Add($"{t.Name}: highest key is {sourceMax} in the source, {targetMax} in the target");
				}

				Console.WriteLine($"  {t.Name,-28} {sourceCount,12} / {targetCount,-12} {(problems.Count == 0 ? "ok" : "DIFFERS")}");
				differences.AddRange(problems);
			}

			return differences;
		}

		// Text columns are compared by byte order on both sides, the two servers sort text differently otherwise
		private static async Task<string> SourceKeyEdge(MySqlConnection source, Table t, bool max)
		{
			string direction = max ? "DESC" : "ASC";
			string order = string.Join(", ", t.Keys.Select(k => (k.IsString ? $"CAST({QM(k.Name)} AS BINARY)" : QM(k.Name)) + " " + direction));
			await using var command = new MySqlCommand($"SELECT {string.Join(", ", t.Keys.Select(k => QM(k.Name)))} FROM {QM(t.Name)} ORDER BY {order} LIMIT 1", source);
			await using var reader = await command.ExecuteReaderAsync();

			if (!await reader.ReadAsync())
			{
				return "none";
			}

			var parts = new List<string>();

			for (int i = 0; i < t.Keys.Count; i++)
			{
				object? value = ConvertValue(t.Keys[i], reader.GetValue(i), out _);
				parts.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL");
			}

			return string.Join(",", parts);
		}

		private static async Task<string> TargetKeyEdge(NpgsqlConnection target, Table t, bool max)
		{
			string direction = max ? "DESC" : "ASC";
			string order = string.Join(", ", t.Keys.Select(k => Q(k.Name) + (k.IsString ? " COLLATE \"C\" " : " ") + direction));
			await using var command = new NpgsqlCommand($"SELECT {string.Join(", ", t.Keys.Select(k => Q(k.Name)))} FROM {Q(t.Name)} ORDER BY {order} LIMIT 1", target);
			await using var reader = await command.ExecuteReaderAsync();

			if (!await reader.ReadAsync())
			{
				return "none";
			}

			var parts = new List<string>();

			for (int i = 0; i < t.Keys.Count; i++)
			{
				parts.Add(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "NULL");
			}

			return string.Join(",", parts);
		}
	}
}
