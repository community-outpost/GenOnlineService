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

using MaxMind.GeoIP2;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentry;
using Sentry.OpenTelemetry;
using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using GenOnlineService.Identity;
using GenOnlineService.Realtime;

namespace GenOnlineService
{
	public static class IPHelpers
	{
		public static string NormalizeIP(string? ipAddress)
		{
			if (string.IsNullOrEmpty(ipAddress))
			{
				return "unknown";
			}

			if (System.Net.IPAddress.TryParse(ipAddress, out System.Net.IPAddress? addr))
			{
				// Convert IPv6-mapped IPv4 (::ffff:127.0.0.1) to IPv4 (127.0.0.1)
				if (addr.IsIPv4MappedToIPv6)
				{
					return addr.MapToIPv4().ToString();
				}

				// Treat all localhost addresses as 127.0.0.1
				if (System.Net.IPAddress.IsLoopback(addr))
				{
					return "127.0.0.1";
				}

				return addr.ToString();
			}

			return ipAddress;
		}
	}

	public static class TokenHelper
	{
		public static Int64 GetUserID(ControllerBase controller)
		{
			var claim = controller.User.FindFirst(ClaimTypes.NameIdentifier);
			if (claim == null || !Int64.TryParse(claim.Value, out Int64 userId))
				return -1;

			return userId;
		}

		public static List<string> GetRoles(ControllerBase controller)
		{
			var roles = controller.User.Claims.Where(c => c.Type == ClaimTypes.Role || c.Type == "role").Select(c => c.Value).ToList();
			return roles;
		}

		// The access token's OAuth client id when it is a registered, enabled client; empty otherwise
		public static string GetClientID(ControllerBase controller)
		{
			return ClientRegistry.FindEnabled(controller.User.FindFirst(SessionTokenHandler.ClientIDClaim)?.Value)?.ClientID ?? String.Empty;
		}

		public static EUserSessionType GetSessionType(ControllerBase controller)
		{
			var first = controller.User.FindFirst("session_type");

			if (first == null)
				return EUserSessionType.None;

			if (int.TryParse(first.Value, out int sessionTypeInt32))
			{
				// Validate if the int corresponds to a defined enum value
				if (System.Enum.IsDefined(typeof(EUserSessionType), sessionTypeInt32))
				{
					EUserSessionType sessionType = (EUserSessionType)sessionTypeInt32;
					return sessionType;
				}
			}

			return EUserSessionType.None;
		}

		public static string GetDisplayName(ControllerBase controller)
		{
			// TODO: Handle not finding claims, it is a critical error
			var first = controller.User.FindFirst(JwtRegisteredClaimNames.Name);
			return first != null ? first.Value : String.Empty;
		}

		public static string GetIPAddress(ControllerBase controller)
		{
			// TODO: Handle not finding claims, it is a critical error
			var first = controller.User.FindFirst(JwtRegisteredClaimNames.Address);
			return first != null ? first.Value : String.Empty;
		}
	}

	public sealed class ReloadingPemCertificateProvider
	{
		private static readonly TimeSpan s_retiredCertificateLifetime = TimeSpan.FromMinutes(5);

		private readonly string m_strCertificatePath;
		private readonly string m_strKeyPath;
		private readonly TimeSpan m_checkInterval;

		private readonly object m_reloadLock = new object();
		private volatile LoadedCertificate? m_current;
		private CertificateFileState? m_lastAttemptedFileState;
		private bool m_bFileStatFailing;
		private long m_lastCheckTimestamp;

		public ReloadingPemCertificateProvider(string strCertificatePath, string strKeyPath, TimeSpan checkInterval)
		{
			m_strCertificatePath = strCertificatePath;
			m_strKeyPath = strKeyPath;
			m_checkInterval = checkInterval;
		}

		public bool TryLoadInitialCertificate()
		{
			try
			{
				ReloadIfChanged(true);
				return true;
			}
			catch (Exception ex) when (IsCertificateLoadException(ex))
			{
				Console.WriteLine($"FATAL ERROR: Failed to load certificate from '{m_strCertificatePath}' and key from '{m_strKeyPath}': {ex}");
				return false;
			}
		}

		public System.Net.Security.SslStreamCertificateContext GetCertificateContext()
		{
			if (IsCheckDue() && Monitor.TryEnter(m_reloadLock))
			{
				// single checker; other handshakes keep using the current cert
				try
				{
					if (IsCheckDue())
					{
						ReloadIfChanged(false);
					}
				}
				finally
				{
					Monitor.Exit(m_reloadLock);
				}
			}

			LoadedCertificate current = m_current ?? throw new InvalidOperationException("No TLS certificate has been loaded.");
			return current.Context;
		}

		private bool IsCheckDue()
		{
			long lastCheck = Interlocked.Read(ref m_lastCheckTimestamp);
			return lastCheck == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(lastCheck) >= m_checkInterval;
		}

		private void ReloadIfChanged(bool bThrowOnFailure)
		{
			lock (m_reloadLock)
			{
				Interlocked.Exchange(ref m_lastCheckTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

				CertificateFileState fileState;
				try
				{
					fileState = CertificateFileState.FromFiles(m_strCertificatePath, m_strKeyPath);
				}
				catch (Exception ex) when (IsCertificateLoadException(ex))
				{
					if (bThrowOnFailure)
					{
						throw;
					}

					// log once per outage, not on every check
					if (!m_bFileStatFailing)
					{
						m_bFileStatFailing = true;
						Console.WriteLine($"ERROR: Failed to stat TLS certificate files. Keeping existing certificate until they are readable again. {ex}");
					}
					return;
				}

				if (m_bFileStatFailing)
				{
					m_bFileStatFailing = false;
					Console.WriteLine("TLS certificate files are readable again.");
				}

				// don't retry a failed pair until the files change again
				if (m_lastAttemptedFileState == fileState && m_current != null)
				{
					return;
				}

				m_lastAttemptedFileState = fileState;

				LoadedCertificate newCertificate;
				try
				{
					newCertificate = LoadedCertificate.Load(m_strCertificatePath, m_strKeyPath);
				}
				catch (Exception ex) when (IsCertificateLoadException(ex))
				{
					if (bThrowOnFailure)
					{
						throw;
					}

					Console.WriteLine($"ERROR: Failed to reload TLS certificate. Keeping existing certificate. {ex}");
					return;
				}

				LoadedCertificate? previousCertificate = m_current;
				m_current = newCertificate;

				X509Certificate2 certificate = newCertificate.Certificate;
				Console.WriteLine($"Loaded TLS certificate thumbprint {certificate.Thumbprint}, valid {certificate.NotBefore:u} through {certificate.NotAfter:u}, with {newCertificate.Intermediates.Count} intermediate certificate(s)");

				if (previousCertificate != null)
				{
					// in-flight handshakes may still hold the old cert
					_ = Task.Run(async () =>
					{
						await Task.Delay(s_retiredCertificateLifetime);
						previousCertificate.Dispose();
					});
				}
			}
		}

		private static bool IsCertificateLoadException(Exception ex)
		{
			return ex is IOException
				|| ex is UnauthorizedAccessException
				|| ex is System.Security.Cryptography.CryptographicException
				|| ex is InvalidOperationException;
		}

		private sealed class LoadedCertificate : IDisposable
		{
			public X509Certificate2 Certificate { get; }
			public X509Certificate2Collection Intermediates { get; }
			public System.Net.Security.SslStreamCertificateContext Context { get; }

			private LoadedCertificate(X509Certificate2 certificate, X509Certificate2Collection intermediates)
			{
				Certificate = certificate;
				Intermediates = intermediates;
				// build the chain once per load instead of on every handshake, sending the PEM's intermediates
				Context = System.Net.Security.SslStreamCertificateContext.Create(certificate, intermediates);
			}

			public static LoadedCertificate Load(string strCertificatePath, string strKeyPath)
			{
				// read each file once so leaf, intermediates and key come from the same snapshot
				string strCertificatePem = File.ReadAllText(strCertificatePath);
				string strKeyPem = File.ReadAllText(strKeyPath);

				X509Certificate2 certificate = X509Certificate2.CreateFromPem(strCertificatePem, strKeyPem);
				X509Certificate2Collection intermediates = new X509Certificate2Collection();
				try
				{
					if (OperatingSystem.IsWindows())
					{
						// SChannel can't use the ephemeral key CreateFromPem produces
						X509Certificate2 persistedCertificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
						certificate.Dispose();
						certificate = persistedCertificate;
					}

					X509Certificate2Collection pemCertificates = new X509Certificate2Collection();
					pemCertificates.ImportFromPem(strCertificatePem);
					foreach (X509Certificate2 pemCertificate in pemCertificates)
					{
						if (pemCertificate.Thumbprint == certificate.Thumbprint)
						{
							pemCertificate.Dispose();
						}
						else
						{
							intermediates.Add(pemCertificate);
						}
					}

					return new LoadedCertificate(certificate, intermediates);
				}
				catch
				{
					DisposeAll(certificate, intermediates);
					throw;
				}
			}

			public void Dispose()
			{
				DisposeAll(Certificate, Intermediates);
			}

			private static void DisposeAll(X509Certificate2 certificate, X509Certificate2Collection intermediates)
			{
				certificate.Dispose();
				foreach (X509Certificate2 intermediate in intermediates)
				{
					intermediate.Dispose();
				}
			}
		}

		private readonly record struct CertificateFileState(
			DateTime CertificateLastWriteUtc,
			long CertificateLength,
			DateTime KeyLastWriteUtc,
			long KeyLength)
		{
			public static CertificateFileState FromFiles(string strCertificatePath, string strKeyPath)
			{
				FileInfo certificateFile = new FileInfo(strCertificatePath);
				if (!certificateFile.Exists)
				{
					throw new FileNotFoundException("TLS certificate PEM file was not found.", strCertificatePath);
				}

				FileInfo keyFile = new FileInfo(strKeyPath);
				if (!keyFile.Exists)
				{
					throw new FileNotFoundException("TLS certificate key file was not found.", strKeyPath);
				}

				return new CertificateFileState(
					certificateFile.LastWriteTimeUtc,
					certificateFile.Length,
					keyFile.LastWriteTimeUtc,
					keyFile.Length);
			}
		}
	}

	public class Program
	{
		private const string BearerSchemeSelector = "go_bearer";


		// Main lobby/websocket tick interval. See the comment at the tick timer for why 50ms is sufficient.
		private const int c_LobbyTickIntervalMS = 50;

		public static IConfiguration? g_Config = null;
		public static DiscordBot? g_Discord = null;

		private enum DbProvider
		{
			MySql,
			Postgres
		}

		// "Database:db_provider": "postgres" (default when the key is absent or empty) or "mysql" ("mariadb" is accepted too).
		private static DbProvider GetDbProvider(IConfiguration config)
		{
			string? value = config.GetSection("Database").GetValue<string>("db_provider");

			if (string.IsNullOrWhiteSpace(value))
			{
				return DbProvider.Postgres;
			}

			switch (value.Trim().ToLowerInvariant())
			{
				case "postgres":
					return DbProvider.Postgres;

				case "mysql":
				case "mariadb":
					return DbProvider.MySql;

				default:
					throw new Exception($"Unsupported Database:db_provider \"{value}\" in config. Supported values are \"postgres\" (default) and \"mysql\" (also \"mariadb\").");
			}
		}

		internal static bool UsesPostgres(IConfiguration config) => GetDbProvider(config) == DbProvider.Postgres;

		private static string BuildDbConnectionString(IConfiguration config, DbProvider provider)
		{
			IConfiguration? dbSettings = config.GetSection("Database");

			if (dbSettings == null)
			{
				throw new Exception("Database section in config is null / not set in config");
			}

			string? hostname = dbSettings.GetValue<string>("db_host");
			string? dbname = dbSettings.GetValue<string>("db_name");
			string? username = dbSettings.GetValue<string>("db_username");
			string? password = dbSettings.GetValue<string>("db_password");
			UInt16 port = dbSettings.GetValue<UInt16?>("db_port") ?? (provider == DbProvider.Postgres ? (UInt16)5432 : (UInt16)3306);

			// Fall back to the connection library's own defaults when a key is absent, rather than silently
			// disabling pooling / zeroing the pool size for deployments predating these settings.
			int db_min_poolsize = dbSettings.GetValue<int?>("db_min_poolsize") ?? 0;
			int db_max_poolsize = dbSettings.GetValue<int?>("db_max_poolsize") ?? 100;
			bool db_use_pooling = dbSettings.GetValue<bool?>("db_use_pooling") ?? true;
			bool db_conn_reset = dbSettings.GetValue<bool?>("db_conn_reset") ?? true;
			int? db_connect_timeout = dbSettings.GetValue<int>("db_connect_timeout");
			int? db_command_timeout = dbSettings.GetValue<int>("db_command_timeout");

			if (hostname == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (dbname == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (username == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (password == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			string? targetSessionAttributes = dbSettings.GetValue<string>("db_target_session_attributes");

			if (provider == DbProvider.Postgres)
			{
				var csb = new Npgsql.NpgsqlConnectionStringBuilder
				{
					Host = hostname,
					Port = port,
					Database = dbname,
					Username = username,
					Password = password,
					Timeout = db_connect_timeout.Value,
					CommandTimeout = db_command_timeout.Value,
					SslMode = Npgsql.SslMode.Prefer,
					Pooling = db_use_pooling,
					MinPoolSize = db_min_poolsize,
					MaxPoolSize = db_max_poolsize,
					NoResetOnClose = !db_conn_reset
				};

				// db_host may list several servers ("a,b,c"); "primary" makes Npgsql pick the writable one after a failover
				if (!string.IsNullOrWhiteSpace(targetSessionAttributes))
				{
					csb.TargetSessionAttributes = targetSessionAttributes;
				}

				return csb.ConnectionString;
			}
			else
			{
				// Refuse instead of ignoring it: the operator expects a failover guarantee that MySQL does not provide here
				if (!string.IsNullOrWhiteSpace(targetSessionAttributes))
				{
					throw new Exception("Database:db_target_session_attributes is a PostgreSQL setting and has no effect with db_provider \"mysql\". Remove it or leave it empty.");
				}

				var csb = new MySqlConnector.MySqlConnectionStringBuilder
				{
					Server = hostname,
					Port = port,
					Database = dbname,
					UserID = username,
					Password = password,
					ConnectionTimeout = (uint)db_connect_timeout.Value,
					DefaultCommandTimeout = (uint)db_command_timeout.Value,
					SslMode = MySqlConnector.MySqlSslMode.Preferred,
					Pooling = db_use_pooling,
					MinimumPoolSize = (uint)db_min_poolsize,
					MaximumPoolSize = (uint)db_max_poolsize,
					ConnectionReset = db_conn_reset,
					// MariaDB allows '0000-00-00' dates (the old schema's defaults and website writes produce them), which would
					// otherwise throw on read. They come back as DateTime.MinValue.
					ConvertZeroDateTime = true
				};

				return csb.ConnectionString;
			}
		}

		private static ServerVersion? DetectMySqlServerVersion(DbProvider provider, string connectionString)
		{
			return provider == DbProvider.MySql ? ServerVersion.AutoDetect(connectionString) : null;
		}

		private static void ConfigureDbContextOptions(DbContextOptionsBuilder options, DbProvider provider, string connectionString, ServerVersion? mySqlServerVersion, SlowDatabaseCommandInterceptor? slowCommandInterceptor = null)
		{
			if (provider == DbProvider.Postgres)
			{
				options.UseNpgsql(connectionString);
			}
			else
			{
				options.UseMySql(connectionString, mySqlServerVersion!);
			}

			options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

			// command events carry SQL text; ignoring them here holds whatever the logging configuration says
			options.ConfigureWarnings(warnings => warnings.Ignore(
				RelationalEventId.CommandExecuting,
				RelationalEventId.CommandExecuted,
				RelationalEventId.CommandError,
				RelationalEventId.CommandCanceled));

			if (slowCommandInterceptor != null)
			{
				options.AddInterceptors(slowCommandInterceptor);
			}

#if RELEASE
			options.EnableSensitiveDataLogging(false); // Ensure sensitive data is not logged
			options.EnableDetailedErrors(false);      // Disable detailed error messages
#endif
		}

		private static AppDbContext CreateDbContext(DbProvider provider, string connectionString, ServerVersion? mySqlServerVersion)
		{
			if (provider == DbProvider.Postgres)
			{
				var optionsBuilder = new DbContextOptionsBuilder<PostgresAppDbContext>();
				ConfigureDbContextOptions(optionsBuilder, provider, connectionString, mySqlServerVersion);
				return new PostgresAppDbContext(optionsBuilder.Options);
			}
			else
			{
				var optionsBuilder = new DbContextOptionsBuilder<MySqlAppDbContext>();
				ConfigureDbContextOptions(optionsBuilder, provider, connectionString, mySqlServerVersion);
				return new MySqlAppDbContext(optionsBuilder.Options);
			}
		}

		private static async Task InitializeDatabase(WebApplicationBuilder builder)
		{
			// TODO_EFCORE: Check connection immediately like old impl
			if (Program.g_Config == null)
			{
				throw new Exception("Config is null. Check config file exists.");
			}

			DbProvider provider = GetDbProvider(Program.g_Config);
			string connectionString = BuildDbConnectionString(Program.g_Config, provider);
			ServerVersion? mySqlServerVersion = DetectMySqlServerVersion(provider, connectionString);

			// TODO_EFCORE: Log exceptions to disk again
			if (!Directory.Exists("Exceptions"))
			{
				Directory.CreateDirectory("Exceptions");
			}

			// EFCore connect
			{
				// TODO_EFCORE: Consider use of ExecuteDeleteAsync and options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
				if (provider == DbProvider.Postgres)
				{
					builder.Services.AddPooledDbContextFactory<PostgresAppDbContext>((serviceProvider, options) => ConfigureDbContextOptions(options, provider, connectionString, mySqlServerVersion, serviceProvider.GetRequiredService<SlowDatabaseCommandInterceptor>()));
					builder.Services.AddSingleton<IDbContextFactory<AppDbContext>, AppDbContextFactoryAdapter<PostgresAppDbContext>>();
				}
				else
				{
					builder.Services.AddPooledDbContextFactory<MySqlAppDbContext>((serviceProvider, options) => ConfigureDbContextOptions(options, provider, connectionString, mySqlServerVersion, serviceProvider.GetRequiredService<SlowDatabaseCommandInterceptor>()));
					builder.Services.AddSingleton<IDbContextFactory<AppDbContext>, AppDbContextFactoryAdapter<MySqlAppDbContext>>();
				}
			}
		}

		// The CLI commands use the same Database section as the service
		internal static IConfiguration OpenConfiguration()
		{
			return WebApplication.CreateBuilder(Array.Empty<string>()).Configuration;
		}

		internal static AppDbContext OpenConfiguredDbContext()
		{
			var config = WebApplication.CreateBuilder(Array.Empty<string>()).Configuration;
			DbProvider provider = GetDbProvider(config);
			string connectionString = BuildDbConnectionString(config, provider);
			return CreateDbContext(provider, connectionString, DetectMySqlServerVersion(provider, connectionString));
		}

		private static bool Confirm(bool assumeYes, string prompt, string nonInteractiveHint)
		{
			if (assumeYes)
			{
				return true;
			}

			if (Console.IsInputRedirected)
			{
				Console.Error.WriteLine(nonInteractiveHint);
				return false;
			}

			Console.Write($"{prompt} [y/N] ");
			string? answer = Console.ReadLine()?.Trim();

			if (!"y".Equals(answer, StringComparison.OrdinalIgnoreCase) && !"yes".Equals(answer, StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine("Aborted.");
				return false;
			}

			return true;
		}

		// "GenOnlineService migrate [--yes]": applies pending migrations without starting the web host, then brings every
		// users.name_key up to date and stops with a report when look-alike names collide (NameKeyBackfill).
		private static async Task<int> RunMigrateCommand(string[] args)
		{
			bool assumeYes = args.Skip(1).Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase));

			try
			{
				var config = WebApplication.CreateBuilder(Array.Empty<string>()).Configuration;

				DbProvider provider = GetDbProvider(config);
				string connectionString = BuildDbConnectionString(config, provider);

				await using var db = CreateDbContext(provider, connectionString, DetectMySqlServerVersion(provider, connectionString));

				var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
				var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

				Console.WriteLine($"Applied migrations: {applied.Count} (latest: {(applied.Count > 0 ? applied[applied.Count - 1] : "none")})");

				if (pending.Count == 0)
				{
					Console.WriteLine("Database is up to date.");
					return await Names.NameKeyBackfill.RunCommand(db, Console.Out, Console.Error);
				}

				Console.WriteLine($"Pending migrations: {pending.Count}");
				foreach (string name in pending)
				{
					Console.WriteLine($"  {name}");
				}

				// the unique (user_id, provider) index would fail on existing duplicates; list them instead of a half-applied migration
				if (applied.Count > 0 && pending.Any(m => m.EndsWith("_UniqueIdentityPerProvider", StringComparison.Ordinal)))
				{
					var duplicates = await db.Identities.AsNoTracking()
						.GroupBy(i => new { i.UserID, i.Provider })
						.Where(g => g.Count() > 1)
						.Select(g => new { g.Key.UserID, g.Key.Provider, Count = g.Count() })
						.ToListAsync();

					if (duplicates.Count > 0)
					{
						Console.Error.WriteLine($"Cannot add the one-identity-per-provider index: {duplicates.Count} user(s) have several identities of one provider. Remove the extra rows from identities first:");
						foreach (var duplicate in duplicates)
						{
							Console.Error.WriteLine($"  user {duplicate.UserID}: {duplicate.Count} {duplicate.Provider} identities");
						}

						return 2;
					}
				}

				if (!Confirm(assumeYes, $"Apply {pending.Count} pending migration(s)?", "Input is not interactive. Pass --yes to apply pending migrations in an unattended run."))
				{
					return 1;
				}

				await db.Database.MigrateAsync();
				Console.WriteLine($"Applied {pending.Count} migration(s) successfully.");
				return await Names.NameKeyBackfill.RunCommand(db, Console.Out, Console.Error);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Migration failed: {ex}");
				return 1;
			}
		}

		// "GenOnlineService import [--dry-run] [--yes]" converts the Import database into the empty Database database (docs/schema.md section 4)
		private static async Task<int> RunImportCommand(string[] args)
		{
			var flags = args.Skip(1).Select(a => a.ToLowerInvariant()).ToList();
			string? unknown = flags.FirstOrDefault(f => f != "--dry-run" && f != "--yes");

			if (unknown != null)
			{
				Console.Error.WriteLine($"Unknown option \"{unknown}\". Usage: GenOnlineService import [--dry-run] [--yes]");
				return 1;
			}

			bool assumeYes = flags.Contains("--yes");

			try
			{
				var config = WebApplication.CreateBuilder(Array.Empty<string>()).Configuration;

				DbProvider provider = GetDbProvider(config);
				Database.ImportSettings settings = Database.Import.ReadSettings(config);
				string connectionString = BuildDbConnectionString(config, provider);

				await using var db = CreateDbContext(provider, connectionString, DetectMySqlServerVersion(provider, connectionString));

				string keyPath = DeviceHasher.GetKeyPath(config);
				if (DeviceHasher.Initialize(keyPath))
				{
					Console.WriteLine($"Created the device HMAC key at {keyPath}. Back it up with the database and give the service the same key.");
				}

				var options = new Database.ImportOptions
				{
					DryRun = flags.Contains("--dry-run"),
					Confirm = (prompt, hint) => Confirm(assumeYes, prompt, hint)
				};

				return await Database.Import.Execute(db, settings, options);
			}
			catch (Database.ImportException ex)
			{
				Console.Error.WriteLine($"Import failed: {ex.Message}");
				return 1;
			}
			catch (Exception ex)
			{
				// Message only: the full exception text can carry connection details
				Console.Error.WriteLine($"Import failed: {ex.GetType().Name}: {ex.Message}");
				Console.Error.WriteLine("The import runs in one transaction, so nothing was written to the target.");
				return 1;
			}
		}

		private static CorsPolicy BuildCorsPolicy(IConfiguration configuration)
		{
			string[] configuredOrigins = configuration
				.GetSection("AllowedOrigins")
				.Get<string[]>() ?? Array.Empty<string>();

			string[] allowedOrigins = configuredOrigins
				.Select(origin => origin.Trim())
				.Where(origin => origin.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

			bool allowAnyOrigin = allowedOrigins.Contains("*");
			if (allowAnyOrigin && allowedOrigins.Length > 1)
			{
				throw new InvalidOperationException("AllowedOrigins cannot combine '*' with explicit origins.");
			}

			var policyBuilder = new CorsPolicyBuilder()
				.AllowAnyHeader()
				.AllowAnyMethod();

			if (allowAnyOrigin)
			{
				// CORS forbids combining any-origin with credential sharing.
				policyBuilder.AllowAnyOrigin();
			}
			else if (allowedOrigins.Length > 0)
			{
				policyBuilder.WithOrigins(allowedOrigins)
					.SetIsOriginAllowedToAllowWildcardSubdomains()
					.AllowCredentials();
			}

			return policyBuilder.Build();
		}

		private static bool TryConfigureForwardedHeaders(IServiceCollection services, IConfiguration configuration)
		{
			string[] trustedProxyValues = (configuration.GetSection("TrustedProxies").Get<string[]>() ?? Array.Empty<string>())
				.Where(value => !string.IsNullOrWhiteSpace(value))
				.Select(value => value.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

			if (trustedProxyValues.Length == 0)
			{
				return false;
			}

			bool trustAnyProxy = trustedProxyValues.Contains("*");
			if (trustAnyProxy && trustedProxyValues.Length > 1)
			{
				throw new InvalidOperationException("TrustedProxies cannot combine '*' with IP addresses or CIDR networks.");
			}

			var trustedProxyAddresses = new List<IPAddress>();
			var trustedProxyNetworks = new List<System.Net.IPNetwork>();

			foreach (string value in trustedProxyValues.Where(value => value != "*"))
			{
				if (IPAddress.TryParse(value, out IPAddress? address))
				{
					trustedProxyAddresses.Add(address);
				}
				else if (System.Net.IPNetwork.TryParse(value, out System.Net.IPNetwork network))
				{
					trustedProxyNetworks.Add(network);
				}
				else
				{
					throw new InvalidOperationException($"TrustedProxies contains invalid IP address or CIDR network '{value}'.");
				}
			}

			services.Configure<ForwardedHeadersOptions>(options =>
			{
				options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
					| ForwardedHeaders.XForwardedProto
					| ForwardedHeaders.XForwardedHost
					| ForwardedHeaders.XForwardedPrefix;
				options.KnownProxies.Clear();
				options.KnownIPNetworks.Clear();

				if (trustAnyProxy)
				{
					return;
				}

				foreach (IPAddress trustedProxyAddress in trustedProxyAddresses)
				{
					options.KnownProxies.Add(trustedProxyAddress);
				}

				foreach (System.Net.IPNetwork trustedProxyNetwork in trustedProxyNetworks)
				{
					options.KnownIPNetworks.Add(trustedProxyNetwork);
				}
			});

			return true;
		}

		// An UpstreamUnavailableException is an expected outage: one Warning line without the stack trace, the next run retries
		internal static async Task RunObservedBackgroundJobAsync(
			string jobName,
			ILogger logger,
			Func<Task> operation)
		{
			bool succeeded = true;
			using Activity? activity = AppMetrics.StartActivity(
				$"background.{jobName}",
				ActivityKind.Internal,
				new KeyValuePair<string, object?>("job.name", jobName));

			try
			{
				await operation();
			}
			catch (UpstreamUnavailableException ex)
			{
				succeeded = false;
				AppMetrics.RecordException(activity, ex);
				logger.LogWarning("Background job {JobName} could not reach its upstream: {Reason}", jobName, ex.Message);
			}
			catch (Exception ex)
			{
				succeeded = false;
				AppMetrics.RecordException(activity, ex);
				logger.LogError(ex, "Background job {JobName} failed", jobName);
			}

			AppMetrics.RecordBackgroundJob(jobName, succeeded);
		}

		// HTTPS enforcement (AccountsSettings.EnforceHttps, on unless Core:enforce_https is false): HSTS and the http to https
		// redirect. A local plain http run sets it to false, which also lets sign-in and its cookies work over http.
		internal static void UseHttpsEnforcement(IApplicationBuilder app, IConfiguration config, ILogger logger)
		{
			if (AccountsSettings.EnforceHttps(config))
			{
				app.UseHsts();

				// WebSocket upgrades are exempt because redirecting an upgrade request breaks the handshake.
				app.UseWhen(context => !context.WebSockets.IsWebSocketRequest, branch =>
				{
					branch.UseHttpsRedirection();
				});
			}
			else
			{
				logger.LogWarning("Core:enforce_https is disabled. Bearer tokens will be sent in clear text over any plain-HTTP listener.");
			}
		}

		public static async Task Main(string[] args)
		{
			if (args.Length > 0 && args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await RunMigrateCommand(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("import", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await RunImportCommand(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("roles", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await ModerationCli.RunRoles(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("bans", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await ModerationCli.RunBans(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("reports", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await ModerationCli.RunReports(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("messages", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await ModerationCli.RunMessages(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("connections", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await ModerationCli.RunConnections(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("realtime", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await ModerationCli.RunRealtime(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("service-accounts", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await ApiTokensCli.RunServiceAccounts(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("tokens", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await ApiTokensCli.RunTokens(args);
				return;
			}

			if (args.Length > 0 && args[0].Equals("clients", StringComparison.OrdinalIgnoreCase))
			{
				Environment.ExitCode = await ClientsCli.RunClients(args);
				return;
			}

#if !DEBUG
			AppDomain.CurrentDomain.UnhandledException += GlobalExceptionHandler;
#endif

			// Configure thread pool for better performance under load
			ThreadPool.SetMinThreads(200, 200);

			var builder = WebApplication.CreateBuilder(args);
			builder.Services.AddSingleton<SlowDatabaseCommandInterceptor>();
			bool enableOpenTelemetry = builder.Configuration.GetValue<bool>("OpenTelemetry:Enabled");
			bool enableSentry = builder.Configuration.GetValue<bool>("Sentry:enabled");
			bool enableSentryOpenTelemetry = enableOpenTelemetry && enableSentry;
			if (enableOpenTelemetry)
			{
				string? openTelemetryEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
				string openTelemetryServiceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? AppMetrics.MeterName;
				double traceSampleRatio = builder.Configuration.GetValue<double?>("OpenTelemetry:TraceSampleRatio") ?? 1.0;
				if (traceSampleRatio < 0 || traceSampleRatio > 1)
				{
					throw new InvalidOperationException("OpenTelemetry:TraceSampleRatio must be between 0 and 1.");
				}

				void ConfigureOtlpExporter(OtlpExporterOptions options)
				{
					if (!String.IsNullOrWhiteSpace(openTelemetryEndpoint))
					{
						options.Endpoint = new Uri(openTelemetryEndpoint);
					}
				}

				builder.Services.AddOpenTelemetry()
					.ConfigureResource(resource => resource.AddService(openTelemetryServiceName))
					.WithMetrics(metrics =>
					{
						metrics.AddMeter(AppMetrics.MeterName);
						metrics.AddMeter(AppMetrics.MySqlMeterName);
						metrics.AddMeter(AppMetrics.NpgsqlMeterName);
						metrics.AddAspNetCoreInstrumentation();
						metrics.AddHttpClientInstrumentation();
						metrics.AddRuntimeInstrumentation();
						metrics.AddOtlpExporter(ConfigureOtlpExporter);
					})
					.WithTracing(tracing =>
					{
						tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(traceSampleRatio)));
						tracing.AddAspNetCoreInstrumentation(options => options.RecordException = true);
						tracing.AddHttpClientInstrumentation(options => options.RecordException = true);
						tracing.AddSource(AppMetrics.ActivitySourceName);
						tracing.AddSource(AppMetrics.MySqlActivitySourceName);
						tracing.AddSource(AppMetrics.NpgsqlActivitySourceName);
						// before Sentry and the exporter, so neither sees unredacted spans
						tracing.AddProcessor(new TelemetryRedactionProcessor());
						if (enableSentryOpenTelemetry)
						{
							tracing.AddSentry();
						}
						tracing.AddOtlpExporter(ConfigureOtlpExporter);
					})
					.WithLogging(logging => logging.AddOtlpExporter(ConfigureOtlpExporter), options =>
					{
						options.IncludeFormattedMessage = true;
						options.IncludeScopes = true;
						options.ParseStateValues = true;
					});
			}
			RoomCatalog.Initialize(Path.Combine(builder.Environment.ContentRootPath, "data", "rooms.json"));

			// Add services to the container.

			g_Config = builder.Configuration;

			// Process the original client IP, request scheme, and host only when
			// one or more trusted reverse proxies are configured.
			bool useForwardedHeaders = TryConfigureForwardedHeaders(builder.Services, builder.Configuration);

			ShowLogo();

			IConfigurationSection? sentrySettings = Program.g_Config.GetSection("Sentry");

			if (sentrySettings == null)
			{
				throw new Exception("Sentry section missing in config");
			}

			bool? sentry_enabled = sentrySettings.GetValue<bool>("enabled");
			string? sentry_dsn = sentrySettings.GetValue<string>("dsn");
			string? sentry_env = sentrySettings.GetValue<string>("environment");

			if (sentry_enabled == null)
			{
				throw new Exception("sentry_enabled missing in config");
			}

			if (sentry_dsn == null)
			{
				throw new Exception("sentry_dsn missing in config");
			}

			if (sentry_env == null)
			{
				sentry_env = "production";
			}

			// Informational version stamped into the build from the git tag (GoResolveVersion in the csproj)
			string buildVersion = GenOnlineService.Rest.Compatibility.ServiceVersion;

			if ((bool)sentry_enabled)
			{
				double sentryTracesSampleRate = sentrySettings.GetValue<double?>("traces_sample_rate") ?? 0.1;
				bool sentryEnableLogs = sentrySettings.GetValue<bool?>("enable_logs") ?? false;

				SentrySdk.Init(options =>
				{
					options.Dsn = sentry_dsn;

					options.Debug = false;

					options.AutoSessionTracking = true;

					options.Environment = sentry_env;

					options.Release = buildVersion;
					options.EnableLogs = sentryEnableLogs;
					// Sentry's own EF/SqlClient listener would put SQL into span descriptions
					options.DisableDiagnosticSourceIntegration();
					if (enableSentryOpenTelemetry)
					{
						options.TracesSampleRate = sentryTracesSampleRate;
						options.UseOpenTelemetry(disableSentryTracing: true);
					}
				});

				builder.Logging.AddSentry(options =>
				{
					options.InitializeSdk = false;
					options.MinimumBreadcrumbLevel = LogLevel.Warning;
					options.MinimumEventLevel = LogLevel.Error;
				});
			}


			// create discord?
			var discordSettings = Program.g_Config.GetSection("Discord");
			bool bEnableDiscord = discordSettings.GetValue<bool>("enable_discord");
			if (bEnableDiscord)
			{
				g_Discord = new DiscordBot();
			}

			builder.Services.AddHostedService<ExternalLeaderboardPublicationWorker>();
			builder.Services.AddSingleton<ILobbyIdSource, DatabaseLobbyIdSource>();
			builder.Services.AddSingleton(sp => new LobbyIdAllocator(sp.GetRequiredService<ILobbyIdSource>()));
			builder.Services.AddSingleton<LobbyManager>();
			builder.Services.AddSingleton<LobbyMembershipConnections>();
			// add a provider by registering another ILinkedAccountProvider
			builder.Services.AddSingleton<GenOnlineService.Rest.ILinkedAccountProvider>(sp => new GenOnlineService.Rest.EosLinkedAccountProvider(sp.GetRequiredService<LobbyManager>()));

			// realtime protocol v1, see docs/realtime-protocol.md; Realtime:backplane redis carries it between copies, see docs/realtime-scaling.md
			GenOnlineService.Realtime.RealtimeBackplaneSettings realtimeBackplane = GenOnlineService.Realtime.RealtimeBackplaneSettings.Read(builder.Configuration);

			// Realtime:failover holds the lease of docs/realtime-failover.md. The host waits the handover budget plus 10 s on shutdown:
			// handover_limit_s, three lease periods for the bounded statements and waits, the switch delay, ten lease periods for the queue move
			// and the forced lobby moves, and 5 s for the last batch (168.5 s with the defaults). A handover cancelled during the shutdown and started again can exceed it, the host then aborts what is left.
			GenOnlineService.Failover.FailoverSettings failoverSettings = GenOnlineService.Failover.FailoverSettings.Read(builder.Configuration);
			if (failoverSettings.Enabled)
			{
				builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(failoverSettings.HandoverBudgetMs + 10_000));
			}
			Microsoft.AspNetCore.SignalR.ISignalRServerBuilder signalR = builder.Services.AddSignalR(options =>
			{
				options.KeepAliveInterval = TimeSpan.FromSeconds(15);
				options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
				options.MaximumReceiveMessageSize = 64 * 1024;
				options.EnableDetailedErrors = false;
				if (failoverSettings.Enabled)
				{
					Microsoft.AspNetCore.SignalR.HubOptionsExtensions.AddFilter(options, new GenOnlineService.Failover.RealtimeFenceFilter());
				}
			}).AddJsonProtocol(options => GenOnlineService.Realtime.RealtimeJson.Configure(options.PayloadSerializerOptions));
			if (realtimeBackplane.UsesRedis)
			{
				signalR.AddStackExchangeRedis(realtimeBackplane.ConfigureSignalR);
			}
			builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, GenOnlineService.Realtime.RealtimeUserIdProvider>();
			builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.HubLifetimeManager<GenOnlineService.Realtime.RealtimeHub>>(services => new RealtimeLifetimeManager(realtimeBackplane.CreateLifetimeManager(services), realtimeBackplane.UsesRedis ? GenOnlineService.Realtime.RealtimeFanout.Groups : null, realtimeBackplane.UsesRedis ? GenOnlineService.Realtime.RealtimeFanout.Outboxes : null));

			CorsPolicy corsPolicy = BuildCorsPolicy(builder.Configuration);

			builder.Services.AddCors(options =>
			{
				options.AddDefaultPolicy(corsPolicy);
			});


			AccountsSettings accountsSettings = AccountsSettings.Read(builder.Configuration);
			builder.Services.AddSingleton(accountsSettings);

			builder.Services.AddSingleton<GenOnlineService.Names.PlayerNames>();

			builder.Services.AddAuthentication(options =>
			{
				options.DefaultScheme = BearerSchemeSelector;
			})
			// gopat- bearer values go to the API token handler, every other bearer value to the OpenIddict access token handler
			.AddPolicyScheme(BearerSchemeSelector, "Bearer", options =>
			{
				options.ForwardDefaultSelector = context => ApiTokenHandler.HasApiToken(context.Request) ? ApiTokenHandler.SchemeName : SessionTokenHandler.SchemeName;
			})
			.AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(ApiTokenHandler.SchemeName, null)
			.AddScheme<AuthenticationSchemeOptions, SessionTokenHandler>(SessionTokenHandler.SchemeName, null);

			builder.Services.AddGenOnlineDataProtection(builder.Configuration, builder.Environment.IsDevelopment());
			builder.Services.AddGenOnlineIdentity(accountsSettings, builder.Environment.IsDevelopment());

			// "perm:<name>" policies, shared by user sessions and API tokens
			builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, PermissionPolicyProvider>();
			builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionHandler>();

			builder.Services.AddAuthorization(options =>
			{
				options.AddPolicy(AuthPolicies.PlayerSessionOrLobbiesRead, policy =>
					policy.RequireAssertion(context =>
						context.User.IsInRole("GameClient")
						|| context.User.IsInRole("ChatClient")
						|| context.User.IsInRole("GameLauncher")
						|| ApiTokenPrincipals.HasPermission(context.User, Permissions.LobbiesRead)));
			});

			// REST controllers answer a failed role check with the forbidden body of section 7
			builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, GenOnlineService.Api.ApiAuthorizationResultHandler>();

			// OpenIddict stores cache entries without a size, so the cache has no size limit
			builder.Services.AddMemoryCache();

			// Add response compression for bandwidth optimization (60-80% reduction)
			builder.Services.AddResponseCompression(options =>
			{
				options.EnableForHttps = true;
				options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
				options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
			});

			builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(options =>
			{
				options.Level = System.IO.Compression.CompressionLevel.Fastest; // Balance speed vs compression
			});

			builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(options =>
			{
				options.Level = System.IO.Compression.CompressionLevel.Fastest;
			});

			// JSON options needed to avoid ASP.NET lower casing everything
			builder.Services.AddControllers().AddJsonOptions(options =>
			{
				options.JsonSerializerOptions
				.PropertyNamingPolicy = null;
			});
			builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
			{
				options.InvalidModelStateResponseFactory = GenOnlineService.Rest.RestSetup.InvalidModelState;
			});
			// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
			//builder.Services.AddOpenApi();

			ReloadingPemCertificateProvider? certificateProvider = null;

			var coreSettings = Program.g_Config.GetSection("Core");

			if (coreSettings == null)
			{
				Console.WriteLine("FATAL ERROR: Core sections of config file is null");
				Console.ReadKey(true);
				return;
			}

			bool use_os_cert_store = coreSettings.GetValue<bool>("use_os_cert_store");
			string? cert_pem_path = coreSettings.GetValue<string>("cert_pem_path");
			string? cert_key_path = coreSettings.GetValue<string>("cert_key_path");

			if (use_os_cert_store == null)
			{
				Console.WriteLine("FATAL ERROR: use_os_cert_store is not set in the config");
				Console.ReadKey(true);
				return;
			}

			if (!use_os_cert_store) // if not using the cert store, we need a pem and key
			{
				if (cert_pem_path == null)
				{
					Console.WriteLine("FATAL ERROR: cert_pem_path is not set in the config");
					Console.ReadKey(true);
					return;
				}

				if (cert_key_path == null)
				{
					Console.WriteLine("FATAL ERROR: cert_key_path is not set in the config");
					Console.ReadKey(true);
					return;
				}
			}


			bool bShouldUseOSCertSTore = (bool)use_os_cert_store;
			List<string> httpsEndpointNames = new List<string>();
			if (!bShouldUseOSCertSTore)
			{
				if (String.IsNullOrEmpty(cert_pem_path) || String.IsNullOrEmpty(cert_key_path))
				{
					Console.WriteLine("FATAL ERROR: use_os_cert_store is set to false, but cert_pem_path and/or cert_key_path were not provided / null!");
					Console.ReadKey(true);
					return;
				}

				int certReloadIntervalSeconds = coreSettings.GetValue<int?>("cert_reload_interval_seconds") ?? 30;
				if (certReloadIntervalSeconds <= 0)
				{
					Console.WriteLine("FATAL ERROR: cert_reload_interval_seconds must be greater than 0");
					Console.ReadKey(true);
					return;
				}

				foreach (IConfigurationSection endpoint in builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
				{
					string? strUrl = endpoint.GetValue<string>("Url");
					if (strUrl != null && strUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
					{
						httpsEndpointNames.Add(endpoint.Key);
					}
				}

				if (httpsEndpointNames.Count == 0)
				{
					Console.WriteLine("FATAL ERROR: use_os_cert_store is set to false, but Kestrel:Endpoints has no https:// endpoint to serve the PEM certificate on");
					Console.ReadKey(true);
					return;
				}

				certificateProvider = new ReloadingPemCertificateProvider(cert_pem_path, cert_key_path, TimeSpan.FromSeconds(certReloadIntervalSeconds));
				if (!certificateProvider.TryLoadInitialCertificate())
				{
					Console.ReadKey(true);
					return;
				}
			}

			// options
			builder.WebHost.ConfigureKestrel(options =>
			{
				options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
				options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);

				if (!bShouldUseOSCertSTore && certificateProvider != null)
				{
					// endpoints (url, Protocols, SslProtocols) stay in Kestrel config; only the cert source is swapped.
					// Kestrel skips its own HTTPS setup for an endpoint that is already TLS.
					Microsoft.AspNetCore.Server.Kestrel.KestrelConfigurationLoader kestrelConfig = options.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: true);
					foreach (string strEndpointName in httpsEndpointNames)
					{
						kestrelConfig.Endpoint(strEndpointName, endpoint =>
						{
							System.Security.Authentication.SslProtocols sslProtocols = endpoint.HttpsOptions.SslProtocols;
							// per-connection callback so rotated certs are picked up without a restart
							endpoint.ListenOptions.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.TlsHandshakeCallbackOptions
							{
								HandshakeTimeout = endpoint.HttpsOptions.HandshakeTimeout,
								OnConnection = _ => ValueTask.FromResult(new System.Net.Security.SslServerAuthenticationOptions
								{
									EnabledSslProtocols = sslProtocols,
									ServerCertificateContext = certificateProvider.GetCertificateContext()
								})
							});
						});
					}
				}

			});

			// add DB
			await InitializeDatabase(builder);

			var app = builder.Build();
			ServiceLocator.Services = app.Services;
			GenOnlineService.Realtime.RealtimeOut.Initialize(app.Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<GenOnlineService.Realtime.RealtimeHub, GenOnlineService.Realtime.IRealtimeClient>>());
			await GenOnlineService.Realtime.RealtimeControl.StartAsync(realtimeBackplane);
			app.Logger.LogInformation("Service copy {CopyId} starting", GenOnlineService.Failover.CopyIdentity.ID);
			GenOnlineService.Failover.FailoverCoordinator? failover = null;
			if (failoverSettings.Enabled)
			{
				GenOnlineService.Failover.RealtimeConnectionDrain drain = new();
				failover = new GenOnlineService.Failover.FailoverCoordinator(failoverSettings, GenOnlineService.Failover.CopyIdentity.ID,
					new GenOnlineService.Failover.DatabaseLeaseStore(app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>()), drain);
				GenOnlineService.Failover.RealtimeFailover.Install(failover);
				if (!realtimeBackplane.UsesRedis)
				{
					app.Logger.LogWarning("Realtime:failover is on without the Redis backplane: during a handover chat between the two copies does not cross, and lobbies and the queue do not survive a failover.");
				}
				else
				{
					// build steps 3 and 4: the owner writes lobbies and the queue to Redis with compared owner epochs, the standby follows them,
					// takes them over after an unplanned failover, and a planned handover moves the queue and the lobbies one by one
					GenOnlineService.Failover.LiveStateSettings liveStateSettings = GenOnlineService.Failover.LiveStateSettings.Read(builder.Configuration);
					GenOnlineService.Failover.RedisLiveStateStore liveStore = new(() => GenOnlineService.Realtime.RealtimeRedis.ConnectAsync(realtimeBackplane), realtimeBackplane.ChannelPrefix, liveStateSettings.StreamMaxLength);
					GenOnlineService.Failover.FailoverCoordinator coordinator = failover;
					GenOnlineService.Failover.LiveStateWriter liveWriter = new(liveStore, GenOnlineService.Failover.CopyIdentity.ID, () => coordinator.OwnsState);
					GenOnlineService.Failover.LiveStateFollower liveFollower = new(liveStore, GenOnlineService.Failover.CopyIdentity.ID,
						() => coordinator.Role is GenOnlineService.Failover.FailoverRole.Standby or GenOnlineService.Failover.FailoverRole.Target, liveStateSettings.PollIntervalMs, failoverSettings.LeaseMs);
					GenOnlineService.Failover.RealtimeLiveState.Install(liveWriter, liveFollower, liveStateSettings.RefreshMs, liveStateSettings.RewriteMs);
					LobbyManager liveLobbies = app.Services.GetRequiredService<LobbyManager>();
					GenOnlineService.Failover.LiveStateHandover handover = new(liveWriter, liveStore, liveLobbies, drain, failoverSettings.LeaseMs * 2);
					liveWriter.Rejected += handover.OnRejected;
					liveFollower.Owned += handover.OnOwned;
					failover.Activating = handover.OnActivating;
					failover.Fencing = handover.OnFencing;
					failover.Mover = handover;
					LobbyManager.MovingLeave = handover.OnMovingLeave;
					failover.StandbyReady = () => liveFollower.InSync;
					GenOnlineService.Failover.RealtimeFailover.InstallSwitcher(drain);

					// the loops run until the process ends, a shutdown handover still writes and follows
					_ = Task.Run(() => liveWriter.RunAsync(CancellationToken.None));
					_ = Task.Run(() => liveFollower.RunAsync(CancellationToken.None));
					app.Logger.LogInformation("Realtime live state on: lobbies and the queue go to Redis, a standby announces itself only while in sync");
				}
			}

			// These log through AppLog, which only has a logger factory once ServiceLocator.Services is set, so they run after Build
			MatchmakingManager.RemoveInvalidPlaylistsAtStartup();
			S3CredentialManager.Initialize();

			// A bad feature name in Features:roles stops the start, an unknown role there is only a warning
			if (!ClientFeatures.Configure(builder.Configuration))
			{
				Console.Error.WriteLine("[ERROR] Features:roles has invalid feature names, see the log above.");
				Environment.ExitCode = 1;
				return;
			}

			// Schema changes only happen through "GenOnlineService migrate"
			using (var migrationScope = app.Services.CreateScope())
			{
				var migrationFactory = migrationScope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using var migrationDb = await migrationFactory.CreateDbContextAsync();
				var pendingMigrations = (await migrationDb.Database.GetPendingMigrationsAsync()).ToList();

				if (pendingMigrations.Count > 0)
				{
					Console.Error.WriteLine($"[ERROR] The database has {pendingMigrations.Count} pending migration(s):");
					foreach (string name in pendingMigrations)
					{
						Console.Error.WriteLine($"  {name}");
					}
					Console.Error.WriteLine("Run \"GenOnlineService migrate\" to apply them, then start the service again.");
					Environment.ExitCode = 1;
					return;
				}
			}

			// Device values are only ever stored as HMACs with this key
			string deviceKeyPath = DeviceHasher.GetKeyPath(builder.Configuration);
			if (DeviceHasher.Initialize(deviceKeyPath))
			{
				app.Logger.LogWarning("Created the device HMAC key at {DeviceKeyPath}. Back it up with the database: without it stored device values can never be matched again.", deviceKeyPath);
			}

			if (useForwardedHeaders)
			{
				// Must run before anything that consumes client IP or request scheme.
				app.UseForwardedHeaders();
			}

			// Enable response compression (must be early in pipeline)
			app.UseResponseCompression();

			// websocket

			var webSocketOptions = new WebSocketOptions
			{
				KeepAliveInterval = TimeSpan.FromSeconds(15),
				KeepAliveTimeout = TimeSpan.FromSeconds(30)
			};

			app.UseWebSockets(webSocketOptions);

			// WebSockets do not use CORS, so apply the same origin policy to browser
			// handshakes explicitly. Native clients may omit Origin.
			app.Use(async (context, next) =>
			{
				if (context.WebSockets.IsWebSocketRequest
					&& context.Request.Headers.TryGetValue("Origin", out var originHeaders)
					&& (originHeaders.Count != 1 || !corsPolicy.IsOriginAllowed(originHeaders[0]!)))
				{
					context.Response.StatusCode = StatusCodes.Status403Forbidden;
					return;
				}

				await next(context);
			});

			// end websocket

			// Configure the HTTP request pipeline.
			/*
			if (app.Environment.IsDevelopment())
			{
				app.MapOpenApi();
			}
			*/

			app.Use((context, next) =>
			{
				context.Request.EnableBuffering();
				return next();
			});

			UseHttpsEnforcement(app, builder.Configuration, app.Logger);

			app.UseCors();
			app.Use(GenOnlineService.Rest.RestSetup.StatusBodies);
			GenOnlineService.Failover.RealtimeFailover.UseRealtimeStandbyGate(app);
			app.UseRealtimeQueryToken();
			app.UseAuthentication();
			app.UseRealtimeRegistrationGate();
			bool bTrustedProxiesConfigured = (builder.Configuration.GetSection("TrustedProxies").Get<string[]>() ?? Array.Empty<string>()).Any(v => !string.IsNullOrWhiteSpace(v));
			app.Use((context, next) =>
			{
				ApiTokenCache.CheckAllowListBehindProxy(context, bTrustedProxiesConfigured);
				return next(context);
			});

			app.UseAuthorization();

			app.MapControllers();
			app.MapHub<GenOnlineService.Realtime.RealtimeHub>(GenOnlineService.Realtime.RealtimeHub.Path, options =>
			{
				options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
				options.ApplicationMaxBufferSize = 64 * 1024;
			});



			// cleanup
			System.Timers.Timer timerCleanup = new System.Timers.Timer(5000); // 5s tick
			timerCleanup.AutoReset = false;
			timerCleanup.Elapsed += async (sender, e) =>
			{
				try
				{
					await RunObservedBackgroundJobAsync("service_cleanup", app.Logger, async () =>
					{
						await WebSocketManager.CheckForTimeouts();

						var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
						int numLobbies = lobbyManager.GetNumLobbies();
						AppMetrics.RecordServiceSnapshot(numLobbies, WebSocketManager.GetNumberOfUsersOnline());
						await lobbyManager.Cleanup();

					});
				}
				finally
				{
					timerCleanup.Start();
				}
			};
			timerCleanup.Start();

			// tick lobby
			// Nothing in the tick needs finer than 50ms resolution: initial lobby syncs are spaced 200ms apart, mesh check
			// snapshots/retries/window are 1s/3s/8s, probes are 30s+, and the dirty-flag coalescing only adds up to one
			// interval of latency. 5ms made every lobby spin ~200x/s while idle.
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(c_LobbyTickIntervalMS);
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
						await lobbyManager.Tick();
						WebSocketManager.Tick();
					}
					catch (Exception ex)
					{
						app.Logger.LogError(ex, "Lobby tick timer failed");
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			// lobby destroy loop - this is separate from the main lobby tick to prevent the tick from being blocked by cleanup
			// @hotfix SkyAero 15/08/2026
			// Event-driven: sleeps on a semaphore released when a lobby is queued for destruction, rather than polling.
			_ = Task.Run(() => ServiceLocator.Services.GetRequiredService<LobbyManager>()
				.RunLobbyDestroyLoopAsync(app.Lifetime.ApplicationStopping));

            // tick matchmaking (done at lower frequency)
            {
				System.Timers.Timer timerTick = new System.Timers.Timer(1000); // 1s tick
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						await MatchmakingManager.Tick();
					}
					catch (Exception ex)
					{
						app.Logger.LogError(ex, "Matchmaking tick timer failed");
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			// Faction win rates are recomputed from today's finished matches every minute
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(60000); // 60s tick
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						await RunObservedBackgroundJobAsync("faction_stats_refresh", app.Logger, async () =>
						{
							using var scope = app.Services.CreateScope();
							var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
							await using var db = await factory.CreateDbContextAsync();
							await Database.FactionStats.Refresh(db, DateTime.UtcNow);
						});
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			// Devices, session events, chat messages and resolved reports past their retention are purged hourly
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds);
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						await RunObservedBackgroundJobAsync("retention_purge", app.Logger, async () =>
						{
							using var scope = app.Services.CreateScope();
							var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
							await using var db = await factory.CreateDbContextAsync();
							try
							{
								var (devices, sessionEvents) = await Database.Retention.Purge(db, DateTime.UtcNow);
								if (devices > 0 || sessionEvents > 0)
								{
									app.Logger.LogInformation("Retention purge removed {DeviceCount} devices and {SessionEventCount} session events", devices, sessionEvents);
								}
							}
							catch (Exception ex)
							{
								app.Logger.LogError(ex, "Device and session event retention purge failed");
							}

							// Runs on its own so a device purge failure never skips it
							try
							{
								var chat = await Database.Chat.PurgeExpired(db, DateTime.UtcNow);
								if (chat.Messages > 0 || chat.ReadMarkers > 0 || chat.Reports > 0 || chat.ConnectionReports > 0)
								{
									app.Logger.LogInformation("Retention purge removed {ChatMessageCount} chat messages, {ChatReadMarkerCount} read markers, {ChatReportCount} resolved reports and {ConnectionReportCount} connection reports", chat.Messages, chat.ReadMarkers, chat.Reports, chat.ConnectionReports);
								}
							}
							catch (Exception ex)
							{
								app.Logger.LogError(ex, "Chat retention purge failed");
							}
						});
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			// Pick up bans and staff roles changed in the database
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(5000); // 5s tick
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						await RunObservedBackgroundJobAsync("access_control_reload", app.Logger, async () =>
						{
							using var scope = app.Services.CreateScope();
							var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
							await using var db = await factory.CreateDbContextAsync();
							await AccessControl.Reload(db);
						});

						// a disabled client is noticed here and its authorizations are revoked
						await RunObservedBackgroundJobAsync("client_registry_reload", app.Logger, async () =>
						{
							using var scope = app.Services.CreateScope();
							var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
							await using var db = await factory.CreateDbContextAsync();
							await ClientRegistry.Reload(db);
						});

						// a separate job, so a failed ban reconcile never keeps a revoked role in place
						await RunObservedBackgroundJobAsync("staff_roles_reload", app.Logger, async () =>
						{
							using var scope = app.Services.CreateScope();
							var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
							await using var db = await factory.CreateDbContextAsync();
							await StaffRoles.Reload(db);
						});

						await RunObservedBackgroundJobAsync("api_tokens_reload", app.Logger, async () =>
						{
							using var scope = app.Services.CreateScope();
							var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
							await using var db = await factory.CreateDbContextAsync();
							await ApiTokenCache.Reload(db);
							await ApiTokenCache.FlushLastUsed(db);
							await ApiTokenCache.Housekeeping(db, ApiTokenSettings.Read(builder.Configuration));
						});

						await RunObservedBackgroundJobAsync("oauth_prune", app.Logger, () => OAuthMaintenance.PruneDaily(app.Services));
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			AppDomain.CurrentDomain.ProcessExit += (_, _) =>
			{
				app.Logger.LogInformation("Exit requested");
			};

			using (var scope = app.Services.CreateScope())
			{
				var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using var db = await factory.CreateDbContextAsync();

				await Database.FactionStats.Refresh(db, DateTime.UtcNow);

				// seeds user state and bans before any token is validated
				await AccessControl.Reload(db);
				await AccessControl.SweepBlockedUsers(factory);
				await StaffRoles.Reload(db);
				StaffRoles.ValidateRoleDefinitions();
				await ApiTokenCache.Reload(db);
				await ClientRegistry.Reload(db);
			}

			if (accountsSettings.Enabled)
			{
				await AccountsSetup.SeedClients(app.Services, accountsSettings);
				await ClientRegistry.SweepDisabledClients(app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>());
			}

			if (failover != null)
			{
				// the stopping callback blocks the shutdown until the handover ends, the server still serves the connections meanwhile
				CancellationTokenSource leaseLoopStop = new();
				_ = Task.Run(() => failover.RunAsync(app.Lifetime.StopApplication, leaseLoopStop.Token));
				app.Lifetime.ApplicationStopping.Register(() =>
				{
					failover.ShutdownAsync().GetAwaiter().GetResult();
					leaseLoopStop.Cancel();
				});
			}

			app.Run();

		}

		public static void ShowLogo()
		{
			ConsoleColor origCol = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Blue;
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("%@          @      @@@@@@@@@@@@       @          @%");
			Console.WriteLine("%=-*%     @+=@    @@@@@@@@@@@@@@@    %=#@    %%*-+#");
			Console.WriteLine(" #=---+*%@#+++#@       @@@@@       %#==*#@#*=---=% ");
			Console.WriteLine("  %+-----=+#*++=+#%@   @@@@@   @%+==++**+=-----+@  ");
			Console.WriteLine("   @%#=------=##++===--=+*==---==++##=------+#%    ");
			Console.WriteLine("    %+--*#+=--=+--+#**+**###***#+--#--==+#+--+%    ");
			Console.WriteLine("     %+-----+++#+---=##++---##=---+*++=-----+%     ");
			Console.WriteLine("      @%*=------+---+#**-::+##=--=+------=*%@      ");
			Console.WriteLine("       @+-=##==-=+===+#--*--#+===+=-==##=-*        ");
			Console.WriteLine("        %*=---=+#*======#+#======*#+----=#%        ");
			Console.WriteLine("         @%#**+=-+=+#++++-++++#==+-=+**#@          ");
			Console.WriteLine("          @#++***#*-=++-:::-*==-##***++%@          ");
			Console.WriteLine("           @#+++++*+--*=-=-++--#*++++*%@           ");
			Console.WriteLine("            @#*+++++###++=++#**+++++*#@            ");
			Console.WriteLine("             @#*++++#-*=+#=-#-**++++%@             ");
			Console.WriteLine("               @***#===--+--+-+*+*#@               ");
			Console.WriteLine("                @%*#-==--+--==-#*%@                ");
			Console.WriteLine("                  %=-==--+--==-+@                  ");
			Console.WriteLine("                   %++--=+-:=++%                   ");
			Console.WriteLine("                    @#+=-+-=+#@                    ");
			Console.WriteLine("                      @%###@                       ");
			Console.WriteLine("                       @@@@@                       ");
			Console.WriteLine("                       @@@@                        ");
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("");
			Console.WriteLine("               GeneralsOnline Service              ");
			Console.ForegroundColor = origCol;
		}

		public static DateTime g_LastStartTime = DateTime.Now;

		public static void GlobalExceptionHandler(object sender, UnhandledExceptionEventArgs e)
		{
			try
			{
				Exception ex = (Exception)e.ExceptionObject;

				if (!Directory.Exists("Exceptions"))
				{
					Directory.CreateDirectory("Exceptions");
				}

				List<string> lstStrings = new List<string>();
				lstStrings.Add(ex.Message);
				lstStrings.Add(ex.ToString());

				if (ex.Source != null)
				{
					lstStrings.Add(ex.Source);
				}

				if (ex.StackTrace != null)
				{
					lstStrings.Add(ex.StackTrace);
				}

				if (ex.InnerException != null)
				{
					lstStrings.Add(ex.InnerException.Message);
					lstStrings.Add(ex.InnerException.ToString());
				}

				string exceptionFileName = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt";

				File.WriteAllLines(Path.Combine("Exceptions", exceptionFileName), lstStrings);
			}
			catch
			{

			}
		}
	}

	public static class ServiceLocator
	{
		public static IServiceProvider Services { get; set; } = default!;
	}

}
