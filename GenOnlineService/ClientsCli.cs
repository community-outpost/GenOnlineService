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

using GenOnlineService.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using static OpenIddict.Abstractions.OpenIddictConstants;
using OidcPermissions = OpenIddict.Abstractions.OpenIddictConstants.Permissions;

namespace GenOnlineService
{
	// "clients" edits the client registry; the running service picks changes up within one reload tick
	public static class ClientsCli
	{
		private const string Usage =
			"Usage:\n"
			+ "  GenOnlineService clients list\n"
			+ "  GenOnlineService clients add <client_id> --kind game|launcher|chat|app --name <display name> --redirect-uris <uri,uri>\n"
			+ "      --third-party true|false [--game zerohour|generals|none] [--type public|confidential] [--scopes <s1,s2>]\n"
			+ "      [--min-version <yyyy.mm.patch>] [--builds <exe_crc:ini_crc,...>]\n"
			+ "  GenOnlineService clients update <client_id> [--name <display name>] [--redirect-uris <uri,uri>] [--scopes <s1,s2>]\n"
			+ "      [--min-version <yyyy.mm.patch>|none] [--builds <exe_crc:ini_crc,...>|none] [--third-party true|false] [--enabled true|false]\n"
			+ "      [--rotate-secret true] [--reason <text>]\n"
			+ "  GenOnlineService clients disable <client_id> [--reason <text>]\n"
			+ "A confidential client's secret is printed once by add and by --rotate-secret.";

		private static readonly string[] s_options =
		{
			"--kind", "--name", "--redirect-uris", "--game", "--type", "--third-party", "--scopes", "--min-version", "--builds", "--enabled", "--rotate-secret", "--reason"
		};

		private sealed class CliException : Exception
		{
			public CliException(string message) : base(message) { }
		}

		// The OpenIddict stores need a factory; each scope opens its own configured context
		private sealed class ConfiguredDbContextFactory : IDbContextFactory<AppDbContext>
		{
			public AppDbContext CreateDbContext() => Program.OpenConfiguredDbContext();
		}

		private sealed record Arguments(List<string> Positional, Dictionary<string, string> Options);

		private static Arguments Parse(string[] args)
		{
			var positional = new List<string>();
			var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			for (int i = 1; i < args.Length; i++)
			{
				if (!args[i].StartsWith("--", StringComparison.Ordinal))
				{
					positional.Add(args[i]);
					continue;
				}

				if (!s_options.Contains(args[i], StringComparer.OrdinalIgnoreCase) || i + 1 >= args.Length)
				{
					throw new CliException($"Unknown option or missing value: {args[i]}{Environment.NewLine}{Usage}");
				}

				options[args[i]] = args[++i];
			}

			return new Arguments(positional, options);
		}

		private static string CliActor()
		{
			string actor = "cli:" + Environment.UserName;
			return actor.Length > 64 ? actor.Substring(0, 64) : actor;
		}

		private static List<string> SplitList(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

		private static bool ParseBool(string option, string value) => value.ToLowerInvariant() switch
		{
			"true" => true,
			"false" => false,
			_ => throw new CliException($"{option} takes true or false.")
		};

		private static string? ParseMinVersion(string value)
		{
			if (value == "none")
			{
				return null;
			}

			string[] parts = value.Split('.');
			if (parts.Length != 3 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
			{
				throw new CliException("--min-version takes yyyy.mm.patch (for example 2026.11.0) or none.");
			}

			return value;
		}

		private static List<AcceptedBuild> ParseBuilds(string value)
		{
			if (value == "none")
			{
				return new List<AcceptedBuild>();
			}

			var builds = new List<AcceptedBuild>();
			foreach (string pair in SplitList(value))
			{
				string[] crcs = pair.Split(':');
				if (crcs.Length != 2 || !TryParseCrc(crcs[0], out uint exe) || !TryParseCrc(crcs[1], out uint ini))
				{
					throw new CliException("--builds takes exe_crc:ini_crc pairs, decimal or 0x hex, separated by commas, or none.");
				}

				builds.Add(new AcceptedBuild(exe, ini));
			}

			return builds.Distinct().ToList();
		}

		private static bool TryParseCrc(string value, out uint crc)
		{
			return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? uint.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out crc)
				: uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out crc);
		}

		// 32 random bytes; OpenIddict stores only its hash
		private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

		private static AuditLogEntry Audit(string action, Int64 targetID, object details, string? reason)
		{
			return new AuditLogEntry
			{
				CreatedAt = DateTime.UtcNow,
				ActorKind = AuditActorKinds.Cli,
				Actor = CliActor(),
				Action = action,
				TargetKind = AuditTargetKinds.Client,
				TargetID = targetID,
				Reason = String.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
				Source = "cli",
				Details = JsonSerializer.Serialize(details)
			};
		}

		public static async Task<int> RunClients(string[] args)
		{
			try
			{
				Arguments parsed = Parse(args);
				if (parsed.Positional.Count == 0)
				{
					throw new CliException(Usage);
				}

				var services = new ServiceCollection();
				services.AddLogging();
				services.AddSingleton<IDbContextFactory<AppDbContext>, ConfiguredDbContextFactory>();
				services.AddOpenIddictCore();
				await using ServiceProvider provider = services.BuildServiceProvider();
				await using AsyncServiceScope scope = provider.CreateAsyncScope();

				AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
				if ((await db.Database.GetPendingMigrationsAsync()).Any())
				{
					throw new CliException("The database has pending migrations. Run \"GenOnlineService migrate\" first.");
				}

				var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
				string command = parsed.Positional[0].ToLowerInvariant();

				switch (command)
				{
					case "list" when parsed.Positional.Count == 1:
						return await List(db, applications);
					case "add" when parsed.Positional.Count == 2:
						return await Add(db, applications, parsed.Positional[1], parsed.Options);
					case "update" when parsed.Positional.Count == 2:
						return await Update(db, applications, parsed.Positional[1], parsed.Options);
					case "disable" when parsed.Positional.Count == 2:
						return await Update(db, applications, parsed.Positional[1], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
						{
							["--enabled"] = "false",
							["--reason"] = parsed.Options.GetValueOrDefault("--reason", String.Empty)
						}, "client_disable");
					default:
						throw new CliException(Usage);
				}
			}
			catch (CliException ex)
			{
				Console.Error.WriteLine(ex.Message);
				return 2;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Failed: {ex.GetType().Name}: {ex.Message}");
				return 1;
			}
		}

		private static async Task<int> List(AppDbContext db, IOpenIddictApplicationManager applications)
		{
			List<Client> clients = await db.Clients.AsNoTracking().OrderBy(c => c.ClientID).ToListAsync();
			ILookup<Int64, ClientBuild> builds = (await db.ClientBuilds.AsNoTracking().ToListAsync()).ToLookup(b => b.ClientDbID);

			Console.WriteLine("client_id\tenabled\tkind\tgame\tparty\ttype\tscopes\tmin_version\tbuilds\tredirect_uris\tname");
			foreach (Client client in clients)
			{
				object? application = await applications.FindByClientIdAsync(client.ClientID);
				string type = application == null ? "missing" : await applications.GetClientTypeAsync(application) ?? "?";
				string name = application == null ? String.Empty : await applications.GetDisplayNameAsync(application) ?? String.Empty;
				string redirects = application == null ? String.Empty : String.Join(",", await applications.GetRedirectUrisAsync(application));
				string scopes = application == null ? String.Empty : String.Join(",", (await applications.GetPermissionsAsync(application))
					.Where(p => p.StartsWith(OidcPermissions.Prefixes.Scope, StringComparison.Ordinal)).Select(p => p.Substring(OidcPermissions.Prefixes.Scope.Length)));
				string accepted = String.Join(",", builds[client.ID].Select(b => $"0x{(uint)b.ExeCrc:X8}:0x{(uint)b.IniCrc:X8}"));

				Console.WriteLine($"{client.ClientID}\t{(client.Enabled ? "enabled" : "disabled")}\t{client.Kind}\t{client.Game}\t{(client.FirstParty ? "first" : "third")}\t{type}\t{scopes}\t{client.MinVersion ?? "-"}\t{(accepted.Length == 0 ? "-" : accepted)}\t{redirects}\t{name}");
			}

			return 0;
		}

		private static async Task<int> Add(AppDbContext db, IOpenIddictApplicationManager applications, string clientID, Dictionary<string, string> options)
		{
			foreach (string forbidden in new[] { "--enabled", "--rotate-secret" })
			{
				if (options.ContainsKey(forbidden))
				{
					throw new CliException($"{forbidden} is an update option; a new client starts enabled.");
				}
			}

			string kind = options.GetValueOrDefault("--kind") ?? throw new CliException("--kind is required." + Environment.NewLine + Usage);
			string name = options.GetValueOrDefault("--name")?.Trim() ?? throw new CliException("--name is required." + Environment.NewLine + Usage);
			string game = options.GetValueOrDefault("--game", ClientGames.None);
			string type = options.GetValueOrDefault("--type", ClientTypes.Public);
			if (type != ClientTypes.Public && type != ClientTypes.Confidential)
			{
				throw new CliException("--type takes public or confidential.");
			}

			bool bConfidential = type == ClientTypes.Confidential;
			// stated on every add, so a third-party app never skips the consent page by omission
			bool bFirstParty = !ParseBool("--third-party", options.GetValueOrDefault("--third-party")
				?? throw new CliException("--third-party true|false is required; a third-party client shows the consent page."));
			List<string> redirectUris = SplitList(options.GetValueOrDefault("--redirect-uris", String.Empty));
			List<string> scopes = options.TryGetValue("--scopes", out string? scopeList) ? SplitList(scopeList) : AccountsSetup.DefaultScopes(kind);
			string? minVersion = options.TryGetValue("--min-version", out string? min) ? ParseMinVersion(min) : null;
			List<AcceptedBuild> builds = options.TryGetValue("--builds", out string? buildList) ? ParseBuilds(buildList) : new List<AcceptedBuild>();

			CheckClient(clientID, kind, game, bConfidential, scopes, redirectUris, name, builds);

			if (await db.Clients.AnyAsync(c => c.ClientID == clientID) || await applications.FindByClientIdAsync(clientID) != null)
			{
				throw new CliException($"Client {clientID} already exists.");
			}

			OpenIddictApplicationDescriptor descriptor = ClientRegistry.Descriptor(clientID, name, bConfidential, kind, bFirstParty, true, redirectUris, scopes);
			string? secret = bConfidential ? NewSecret() : null;
			descriptor.ClientSecret = secret;

			DateTime now = DateTime.UtcNow;
			var row = new Client { ClientID = clientID, Kind = kind, Game = game, FirstParty = bFirstParty, Enabled = true, MinVersion = minVersion, CreatedAt = now, UpdatedAt = now };

			// the OpenIddict row, the client row, its builds and the audit entry commit together
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				await applications.CreateAsync(descriptor);
				db.Clients.Add(row);
				await db.SaveChangesAsync();
				db.ClientBuilds.AddRange(builds.Select(b => new ClientBuild { ClientDbID = row.ID, ExeCrc = b.ExeCrc, IniCrc = b.IniCrc }));
				db.AuditLog.Add(Audit("client_add", row.ID, new
				{
					client_id = clientID, name, kind, game, type, first_party = bFirstParty, scopes, redirect_uris = redirectUris, min_version = minVersion,
					builds = builds.Select(b => new { exe_crc = b.ExeCrc, ini_crc = b.IniCrc })
				}, options.GetValueOrDefault("--reason")));
				await db.SaveChangesAsync();
				await tx.CommitAsync();
			}

			Console.WriteLine($"Added client {clientID} ({row.ID}).");
			if (secret != null)
			{
				Console.WriteLine($"Client secret (shown once): {secret}");
			}

			return 0;
		}

		private static async Task<int> Update(AppDbContext db, IOpenIddictApplicationManager applications, string clientID, Dictionary<string, string> options, string action = "client_update")
		{
			if (options.Keys.Any(k => k is "--kind" or "--game" or "--type"))
			{
				throw new CliException("--kind, --game and --type are fixed; add a new client instead.");
			}

			Client row = await db.Clients.AsTracking().FirstOrDefaultAsync(c => c.ClientID == clientID) ?? throw new CliException($"Client {clientID} does not exist.");
			object application = await applications.FindByClientIdAsync(clientID) ?? throw new CliException($"Client {clientID} has no OAuth application row.");

			bool bConfidential = await applications.HasClientTypeAsync(application, ClientTypes.Confidential);
			string name = options.TryGetValue("--name", out string? newName) ? newName.Trim() : await applications.GetDisplayNameAsync(application) ?? clientID;
			List<string> redirectUris = options.TryGetValue("--redirect-uris", out string? uris) ? SplitList(uris)
				: (await applications.GetRedirectUrisAsync(application)).ToList();
			List<string> scopes = options.TryGetValue("--scopes", out string? scopeList) ? SplitList(scopeList)
				: (await applications.GetPermissionsAsync(application)).Where(p => p.StartsWith(OidcPermissions.Prefixes.Scope, StringComparison.Ordinal))
					.Select(p => p.Substring(OidcPermissions.Prefixes.Scope.Length)).Prepend(Scopes.OpenId).ToList();
			bool bFirstParty = options.TryGetValue("--third-party", out string? thirdParty) ? !ParseBool("--third-party", thirdParty) : row.FirstParty;
			bool bEnabled = options.TryGetValue("--enabled", out string? enabled) ? ParseBool("--enabled", enabled) : row.Enabled;
			string? minVersion = options.TryGetValue("--min-version", out string? min) ? ParseMinVersion(min) : row.MinVersion;
			bool bRotate = options.TryGetValue("--rotate-secret", out string? rotate) && ParseBool("--rotate-secret", rotate);

			List<ClientBuild> currentBuilds = await db.ClientBuilds.AsTracking().Where(b => b.ClientDbID == row.ID).ToListAsync();
			List<AcceptedBuild> builds = options.TryGetValue("--builds", out string? buildList) ? ParseBuilds(buildList)
				: currentBuilds.Select(b => new AcceptedBuild((uint)b.ExeCrc, (uint)b.IniCrc)).ToList();

			if (bRotate && !bConfidential)
			{
				throw new CliException("Only a confidential client has a secret to rotate.");
			}

			CheckClient(clientID, row.Kind, row.Game, bConfidential, scopes, redirectUris, name, builds);

			OpenIddictApplicationDescriptor descriptor = new();
			await applications.PopulateAsync(descriptor, application);
			OpenIddictApplicationDescriptor next = ClientRegistry.Descriptor(clientID, name, bConfidential, row.Kind, bFirstParty, bEnabled, redirectUris, scopes);

			// the stored secret hash is kept unless it is rotated
			string? secret = bRotate ? NewSecret() : null;
			next.ClientSecret = secret ?? descriptor.ClientSecret;

			var changes = new Dictionary<string, object?>();
			if (name != descriptor.DisplayName) changes["name"] = name;
			if (!redirectUris.ToHashSet().SetEquals(descriptor.RedirectUris.Select(u => u.OriginalString))) changes["redirect_uris"] = redirectUris;
			if (!next.Permissions.SetEquals(descriptor.Permissions) && options.ContainsKey("--scopes")) changes["scopes"] = scopes;
			if (bFirstParty != row.FirstParty) changes["first_party"] = bFirstParty;
			if (bEnabled != row.Enabled) changes["enabled"] = bEnabled;
			if (minVersion != row.MinVersion) changes["min_version"] = minVersion;
			if (!builds.ToHashSet().SetEquals(currentBuilds.Select(b => new AcceptedBuild((uint)b.ExeCrc, (uint)b.IniCrc)))) changes["builds"] = builds.Select(b => new { exe_crc = b.ExeCrc, ini_crc = b.IniCrc });
			if (bRotate) changes["secret"] = "rotated";

			if (changes.Count == 0)
			{
				Console.WriteLine($"Client {clientID} is unchanged.");
				return 0;
			}

			row.FirstParty = bFirstParty;
			row.Enabled = bEnabled;
			row.MinVersion = minVersion;
			row.UpdatedAt = DateTime.UtcNow;

			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				await applications.UpdateAsync(application, next);
				if (changes.ContainsKey("builds"))
				{
					db.ClientBuilds.RemoveRange(currentBuilds);
					db.ClientBuilds.AddRange(builds.Select(b => new ClientBuild { ClientDbID = row.ID, ExeCrc = b.ExeCrc, IniCrc = b.IniCrc }));
				}

				db.AuditLog.Add(Audit(action, row.ID, new { client_id = clientID, changes }, options.GetValueOrDefault("--reason")));
				await db.SaveChangesAsync();
				await tx.CommitAsync();
			}

			Console.WriteLine($"Updated client {clientID}: {String.Join(", ", changes.Keys)}.");
			if (!bEnabled && changes.ContainsKey("enabled"))
			{
				Console.WriteLine("The running service revokes its sign-ins within one reload tick.");
			}

			if (secret != null)
			{
				Console.WriteLine($"Client secret (shown once): {secret}");
			}

			return 0;
		}

		private static void CheckClient(string clientID, string kind, string game, bool bConfidential, List<string> scopes, List<string> redirectUris, string name, List<AcceptedBuild> builds)
		{
			string? error = ClientRegistry.Validate(clientID, kind, game, bConfidential, scopes, redirectUris);
			if (error != null)
			{
				throw new CliException(error);
			}

			string? details = ClientRegistry.ValidateDetails(name, null);
			if (details != null)
			{
				throw new CliException(details);
			}

			if (builds.Count > 0 && kind != ClientKinds.Game)
			{
				throw new CliException("Only a game client lists accepted builds.");
			}

			if (!scopes.Contains(Scopes.OpenId))
			{
				throw new CliException("Every client needs the openid scope.");
			}
		}
	}
}
