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

using GenOnlineService.Rest;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GenOnlineService
{
	// "service-accounts" and "tokens" write the database directly; the only way to grant privileged permissions or mint tokens carrying them
	public static class ApiTokensCli
	{
		private const string ServiceAccountsUsage =
			"Usage:\n"
			+ "  GenOnlineService service-accounts list\n"
			+ "  GenOnlineService service-accounts create <name> [--description <text>]\n"
			+ "  GenOnlineService service-accounts grant <name> <permission>\n"
			+ "  GenOnlineService service-accounts revoke-grant <name> <permission>\n"
			+ "  GenOnlineService service-accounts disable <name>\n"
			+ "  GenOnlineService service-accounts enable <name>";

		private const string TokensUsage =
			"Usage:\n"
			+ "  GenOnlineService tokens list [--owner service:<name>|user:<id>] [--all true]\n"
			+ "  GenOnlineService tokens create --owner service:<name>|user:<id> --name <name> --scopes <p1,p2> --expires <yyyy-mm-dd> [--allowed-ips <cidr,cidr>]\n"
			+ "  GenOnlineService tokens rotate <id> [--overlap-minutes <0-1440>] [--expires <yyyy-mm-dd>]\n"
			+ "  GenOnlineService tokens revoke <id> [--reason <text>]";

		private sealed class CliException : Exception
		{
			public CliException(string message) : base(message) { }
		}

		private sealed class Arguments
		{
			public List<string> Positional = new();
			public Dictionary<string, string> Options = new(StringComparer.OrdinalIgnoreCase);
		}

		private static Arguments Parse(string[] args, string usage, params string[] allowedOptions)
		{
			var result = new Arguments();

			for (int i = 1; i < args.Length; i++)
			{
				if (!args[i].StartsWith("--", StringComparison.Ordinal))
				{
					result.Positional.Add(args[i]);
					continue;
				}

				if (!allowedOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase) || i + 1 >= args.Length)
				{
					throw new CliException($"Unknown option or missing value: {args[i]}{Environment.NewLine}{usage}");
				}

				result.Options[args[i]] = args[++i];
			}

			return result;
		}

		private static readonly ApiTokenActor s_actor = ApiTokenActor.Cli();

		private static async Task<int> Run(Func<Task<int>> command)
		{
			try
			{
				return await command();
			}
			catch (CliException ex)
			{
				Console.Error.WriteLine(ex.Message);
				return 2;
			}
			catch (ApiTokenAdminException ex)
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

		private static async Task<AppDbContext> OpenDatabase()
		{
			AppDbContext db = Program.OpenConfiguredDbContext();

			if ((await db.Database.GetPendingMigrationsAsync()).Any())
			{
				await db.DisposeAsync();
				throw new CliException("The database has pending migrations. Run \"GenOnlineService migrate\" first.");
			}

			return db;
		}

		private static async Task<ServiceAccount> RequireServiceAccount(AppDbContext db, string name)
		{
			return await db.ServiceAccounts.AsTracking().FirstOrDefaultAsync(a => a.Name == name)
				?? throw new CliException($"Service account {name} does not exist.");
		}

		public static Task<int> RunServiceAccounts(string[] args)
		{
			return Run(async () =>
			{
				Arguments parsed = Parse(args, ServiceAccountsUsage, "--description");
				if (parsed.Positional.Count == 0)
				{
					throw new CliException(ServiceAccountsUsage);
				}

				await using AppDbContext db = await OpenDatabase();
				string command = parsed.Positional[0].ToLowerInvariant();

				switch (command)
				{
					case "list" when parsed.Positional.Count == 1:
					{
						List<ServiceAccount> accounts = await db.ServiceAccounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
						List<ServiceAccountPermission> grants = await db.ServiceAccountPermissions.AsNoTracking().ToListAsync();
						foreach (ServiceAccount account in accounts)
						{
							string permissions = String.Join(",", grants.Where(g => g.ServiceAccountID == account.ID).Select(g => g.Permission).OrderBy(p => p, StringComparer.Ordinal));
							Console.WriteLine($"{account.ID}\t{account.Name}\t{(account.DisabledAt == null ? "active" : "disabled")}\t{permissions}\t{account.Description}");
						}
						return 0;
					}

					case "create" when parsed.Positional.Count == 2:
					{
						string name = parsed.Positional[1];
						string description = parsed.Options.GetValueOrDefault("--description", String.Empty).Trim();
						ServiceAccount account = await ApiTokenAdmin.CreateServiceAccount(db, s_actor, name, description);
						Console.WriteLine($"Created service account {name} ({account.ID}).");
						return 0;
					}

					case "grant" when parsed.Positional.Count == 3:
					case "revoke-grant" when parsed.Positional.Count == 3:
					{
						ServiceAccount account = await RequireServiceAccount(db, parsed.Positional[1]);
						string permission = ApiTokenAdmin.RequirePermission(parsed.Positional[2], "permission");

						if (command == "grant")
						{
							if (!await ApiTokenAdmin.StageGrant(db, s_actor, account, permission))
							{
								Console.WriteLine($"{account.Name} already holds {permission}.");
								return 0;
							}
						}
						else
						{
							if (!await ApiTokenAdmin.StageRevokeGrant(db, s_actor, account, permission))
							{
								Console.WriteLine($"{account.Name} does not hold {permission}.");
								return 0;
							}
						}

						await db.SaveChangesAsync();
						Console.WriteLine($"{(command == "grant" ? "Granted" : "Revoked")} {permission} {(command == "grant" ? "to" : "from")} {account.Name}.");
						return 0;
					}

					case "disable" when parsed.Positional.Count == 2:
					case "enable" when parsed.Positional.Count == 2:
					{
						ServiceAccount account = await RequireServiceAccount(db, parsed.Positional[1]);
						ApiTokenAdmin.StageSetDisabled(db, s_actor, account, command == "disable");
						await db.SaveChangesAsync();
						Console.WriteLine($"{account.Name} is {(command == "disable" ? "disabled" : "enabled")}.");
						return 0;
					}

					default:
						throw new CliException(ServiceAccountsUsage);
				}
			});
		}

		public static Task<int> RunTokens(string[] args)
		{
			return Run(async () =>
			{
				Arguments parsed = Parse(args, TokensUsage, "--owner", "--name", "--scopes", "--expires", "--allowed-ips", "--overlap-minutes", "--reason", "--all");
				if (parsed.Positional.Count == 0)
				{
					throw new CliException(TokensUsage);
				}

				await using AppDbContext db = await OpenDatabase();
				ApiTokenSettings settings = ApiTokenSettings.Read(Program.OpenConfiguration());

				switch (parsed.Positional[0].ToLowerInvariant())
				{
					case "list" when parsed.Positional.Count == 1:
						return await List(db, parsed);
					case "create" when parsed.Positional.Count == 1:
						return await Create(db, settings, parsed);
					case "rotate" when parsed.Positional.Count == 2:
						return await Rotate(db, settings, parsed);
					case "revoke" when parsed.Positional.Count == 2:
						return await Revoke(db, parsed);
					default:
						throw new CliException(TokensUsage);
				}
			});
		}

		private static async Task<ApiTokenOwner> ResolveOwner(AppDbContext db, string text)
		{
			if (text.StartsWith("service:", StringComparison.Ordinal))
			{
				return ApiTokenOwner.Service(await RequireServiceAccount(db, text.Substring("service:".Length)));
			}

			if (text.StartsWith("user:", StringComparison.Ordinal)
				&& Int64.TryParse(text.AsSpan("user:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out Int64 userID) && userID > 0)
			{
				if (!await db.Users.AsNoTracking().AnyAsync(u => u.ID == userID))
				{
					throw new CliException($"User {userID} does not exist.");
				}

				return ApiTokenOwner.User(userID);
			}

			throw new CliException("--owner is service:<name> or user:<id>.");
		}

		private static DateTime ParseExpiry(string text)
		{
			if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
			{
				throw new CliException($"--expires must be a date as yyyy-mm-dd: {text}");
			}

			return DateTime.SpecifyKind(date, DateTimeKind.Utc);
		}

		private static async Task<int> Create(AppDbContext db, ApiTokenSettings settings, Arguments parsed)
		{
			ApiTokenOwner owner = await ResolveOwner(db, parsed.Options.GetValueOrDefault("--owner") ?? throw new CliException("--owner is required."));
			string name = parsed.Options.GetValueOrDefault("--name", String.Empty).Trim();
			List<string> scopes = ApiTokenFormat.ParseScopes(parsed.Options.GetValueOrDefault("--scopes", String.Empty));
			string? allowedIPs = parsed.Options.GetValueOrDefault("--allowed-ips");

			(ApiToken token, string secret) = await ApiTokenAdmin.Mint(db, settings, s_actor, owner, name, scopes, allowedIPs,
				() => ParseExpiry(parsed.Options.GetValueOrDefault("--expires") ?? throw new CliException("--expires is required.")), bFromHost: true);

			Console.WriteLine($"Token {token.ID} ({name}) for {owner.Label}, expires {token.ExpiresAt:yyyy-MM-dd}. It is shown once:");
			Console.WriteLine(secret);
			return 0;
		}

		private static async Task<ApiToken> RequireToken(AppDbContext db, string text)
		{
			if (!Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out Int64 tokenID))
			{
				throw new CliException($"Not a token id: {text}");
			}

			return await db.ApiTokens.AsTracking().FirstOrDefaultAsync(t => t.ID == tokenID) ?? throw new CliException($"Token {tokenID} does not exist.");
		}

		private static async Task<int> Rotate(AppDbContext db, ApiTokenSettings settings, Arguments parsed)
		{
			ApiToken old = await RequireToken(db, parsed.Positional[1]);
			int overlap = 0;

			(ApiToken token, string secret) = await ApiTokenAdmin.Rotate(db, settings, s_actor, old,
				() => overlap = ApiTokenAdmin.CheckOverlap(parsed.Options.GetValueOrDefault("--overlap-minutes")),
				() => parsed.Options.TryGetValue("--expires", out string? expiresText) ? ParseExpiry(expiresText) : null);

			Console.WriteLine($"Token {token.ID} replaces {old.ID}, which stops {(overlap == 0 ? "now" : $"in {overlap} minutes")}. The new token is shown once:");
			Console.WriteLine(secret);
			return 0;
		}

		private static async Task<int> Revoke(AppDbContext db, Arguments parsed)
		{
			ApiToken token = await RequireToken(db, parsed.Positional[1]);
			string reason = parsed.Options.GetValueOrDefault("--reason", String.Empty).Trim();

			Console.WriteLine(await ApiTokenAdmin.Revoke(db, s_actor, token, reason) ? $"Token {token.ID} is revoked." : $"Token {token.ID} is already revoked.");
			return 0;
		}

		private static async Task<int> List(AppDbContext db, Arguments parsed)
		{
			IQueryable<ApiToken> query = db.ApiTokens.AsNoTracking();
			if (parsed.Options.TryGetValue("--owner", out string? ownerText))
			{
				query = ApiTokenAdmin.OwnedBy(query, await ResolveOwner(db, ownerText));
			}

			DateTime now = DateTime.UtcNow;
			if (!parsed.Options.ContainsKey("--all"))
			{
				query = query.Where(t => t.ExpiresAt > now && (t.RevokedAt == null || t.RevokedAt > now));
			}

			Dictionary<Int64, string> names = await db.ServiceAccounts.AsNoTracking().ToDictionaryAsync(a => a.ID, a => a.Name);
			foreach (ApiToken token in await query.OrderBy(t => t.ID).ToListAsync())
			{
				string owner = token.OwnerServiceID != null ? "service:" + names.GetValueOrDefault(token.OwnerServiceID.Value) : "user:" + token.OwnerUserID;
				string state = token.RevokedAt != null && token.RevokedAt <= now ? "revoked" : token.ExpiresAt <= now ? "expired" : "active";
				Console.WriteLine($"{token.ID}\t{token.Name}\t{owner}\t...{token.TokenLast4}\t{state}\texpires {token.ExpiresAt:yyyy-MM-dd}\t{token.Scopes}\t{token.AllowedIPs ?? "-"}\tlast used {(token.LastUsedAt?.ToString("u") ?? "never")} {token.LastUsedIP}");
			}

			return 0;
		}
	}
}
