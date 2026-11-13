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
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

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

		private const int MaximumOverlapMinutes = 1440;

		private static readonly Regex s_serviceAccountName = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

		private sealed class CliException : Exception
		{
			public CliException(string message) : base(message) { }
		}

		private sealed class Arguments
		{
			public List<string> Positional = new();
			public Dictionary<string, string> Options = new(StringComparer.OrdinalIgnoreCase);
		}

		private sealed record Owner(string Kind, Int64 ID, string Label);

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

		private static string CliActor() => "cli:" + Environment.UserName;

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

		private static AuditLogEntry Audit(string action, string targetKind, Int64 targetID, object details, string? reason = null)
		{
			return new AuditLogEntry
			{
				CreatedAt = DateTime.UtcNow,
				ActorKind = AuditActorKinds.Cli,
				Actor = CliActor().Length > 64 ? CliActor().Substring(0, 64) : CliActor(),
				Action = action,
				TargetKind = targetKind,
				TargetID = targetID,
				Reason = String.IsNullOrEmpty(reason) ? null : reason,
				Source = "cli",
				Details = JsonSerializer.Serialize(details)
			};
		}

		private static async Task<ServiceAccount> RequireServiceAccount(AppDbContext db, string name)
		{
			return await db.ServiceAccounts.AsTracking().FirstOrDefaultAsync(a => a.Name == name)
				?? throw new CliException($"Service account {name} does not exist.");
		}

		private static string RequirePermission(string permission)
		{
			if (!Permissions.IsKnown(permission))
			{
				throw new CliException($"Unknown permission: {permission}. Known: {String.Join(", ", Permissions.All.OrderBy(p => p, StringComparer.Ordinal))}");
			}

			return permission;
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
						if (!s_serviceAccountName.IsMatch(name))
						{
							throw new CliException("A service account name is 1 to 64 characters of a-z, 0-9, '-' and '_', starting with a letter or digit.");
						}

						string description = parsed.Options.GetValueOrDefault("--description", String.Empty).Trim();
						if (description.Length > 256)
						{
							throw new CliException("The description must be 256 characters or fewer.");
						}

						if (await db.ServiceAccounts.AnyAsync(a => a.Name == name))
						{
							throw new CliException($"Service account {name} already exists.");
						}

						var account = new ServiceAccount { Name = name, Description = description, CreatedBy = CliActor(), CreatedAt = DateTime.UtcNow };
						db.ServiceAccounts.Add(account);
						// the row and its audit entry commit together
						await using (var tx = await db.Database.BeginTransactionAsync())
						{
							await db.SaveChangesAsync();
							db.AuditLog.Add(Audit("sa_create", AuditTargetKinds.ServiceAccount, account.ID, new { name, description }));
							await db.SaveChangesAsync();
							await tx.CommitAsync();
						}
						Console.WriteLine($"Created service account {name} ({account.ID}).");
						return 0;
					}

					case "grant" when parsed.Positional.Count == 3:
					case "revoke-grant" when parsed.Positional.Count == 3:
					{
						ServiceAccount account = await RequireServiceAccount(db, parsed.Positional[1]);
						string permission = RequirePermission(parsed.Positional[2]);
						ServiceAccountPermission? existing = await db.ServiceAccountPermissions.FirstOrDefaultAsync(p => p.ServiceAccountID == account.ID && p.Permission == permission);

						if (command == "grant")
						{
							if (existing != null)
							{
								Console.WriteLine($"{account.Name} already holds {permission}.");
								return 0;
							}

							db.ServiceAccountPermissions.Add(new ServiceAccountPermission { ServiceAccountID = account.ID, Permission = permission, GrantedBy = CliActor(), GrantedAt = DateTime.UtcNow });
							db.AuditLog.Add(Audit("sa_grant", AuditTargetKinds.ServiceAccount, account.ID, new { permission, privileged = Permissions.IsPrivileged(permission) }));
						}
						else
						{
							if (existing == null)
							{
								Console.WriteLine($"{account.Name} does not hold {permission}.");
								return 0;
							}

							db.ServiceAccountPermissions.Remove(existing);
							db.AuditLog.Add(Audit("sa_revoke", AuditTargetKinds.ServiceAccount, account.ID, new { permission }));
						}

						await db.SaveChangesAsync();
						Console.WriteLine($"{(command == "grant" ? "Granted" : "Revoked")} {permission} {(command == "grant" ? "to" : "from")} {account.Name}.");
						return 0;
					}

					case "disable" when parsed.Positional.Count == 2:
					case "enable" when parsed.Positional.Count == 2:
					{
						ServiceAccount account = await RequireServiceAccount(db, parsed.Positional[1]);
						account.DisabledAt = command == "disable" ? DateTime.UtcNow : null;
						db.AuditLog.Add(Audit(command == "disable" ? "sa_disable" : "sa_enable", AuditTargetKinds.ServiceAccount, account.ID, new { name = account.Name }));
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

		private static async Task<Owner> ResolveOwner(AppDbContext db, string text)
		{
			if (text.StartsWith("service:", StringComparison.Ordinal))
			{
				ServiceAccount account = await RequireServiceAccount(db, text.Substring("service:".Length));
				return new Owner(ApiTokenOwnerKinds.ServiceAccount, account.ID, "service:" + account.Name);
			}

			if (text.StartsWith("user:", StringComparison.Ordinal)
				&& Int64.TryParse(text.AsSpan("user:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out Int64 userID) && userID > 0)
			{
				if (!await db.Users.AsNoTracking().AnyAsync(u => u.ID == userID))
				{
					throw new CliException($"User {userID} does not exist.");
				}

				return new Owner(ApiTokenOwnerKinds.User, userID, "user:" + userID);
			}

			throw new CliException("--owner is service:<name> or user:<id>.");
		}

		// What the owner holds right now; a token never grants more
		private static async Task<HashSet<string>> OwnerPermissions(AppDbContext db, Owner owner)
		{
			if (owner.Kind == ApiTokenOwnerKinds.ServiceAccount)
			{
				return (await db.ServiceAccountPermissions.AsNoTracking().Where(p => p.ServiceAccountID == owner.ID).Select(p => p.Permission).ToListAsync())
					.ToHashSet(StringComparer.Ordinal);
			}

			List<string> roles = await db.UserRoles.AsNoTracking().Where(r => r.UserID == owner.ID).Select(r => r.Role).ToListAsync();
			return StaffRoles.PermissionsFor(roles);
		}

		private static DateTime ParseExpiry(string text)
		{
			if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
			{
				throw new CliException($"--expires must be a date as yyyy-mm-dd: {text}");
			}

			return DateTime.SpecifyKind(date, DateTimeKind.Utc);
		}

		private static void CheckLifetime(ApiTokenSettings settings, IReadOnlyCollection<string> scopes, string? allowedIPs, DateTime expiresAt)
		{
			DateTime now = DateTime.UtcNow;
			if (expiresAt <= now)
			{
				throw new CliException("The expiry date must be in the future.");
			}

			bool bPrivileged = scopes.Any(Permissions.IsPrivileged);
			int maxDays = bPrivileged ? settings.PrivilegedMaxLifetimeDays : settings.MaxLifetimeDays;
			if (expiresAt > now.AddDays(maxDays))
			{
				throw new CliException($"{(bPrivileged ? "A token with a privileged scope" : "A token")} may live at most {maxDays} days.");
			}

			if (bPrivileged && ApiTokenFormat.ParseAllowedIPs(allowedIPs).Count == 0)
			{
				throw new CliException("A token with a privileged scope needs --allowed-ips.");
			}
		}

		private static async Task CheckActiveLimit(AppDbContext db, ApiTokenSettings settings, Owner owner)
		{
			DateTime now = DateTime.UtcNow;
			IQueryable<ApiToken> active = db.ApiTokens.AsNoTracking().Where(t => t.ExpiresAt > now && (t.RevokedAt == null || t.RevokedAt > now));
			active = owner.Kind == ApiTokenOwnerKinds.ServiceAccount ? active.Where(t => t.OwnerServiceID == owner.ID) : active.Where(t => t.OwnerUserID == owner.ID);

			if (await active.CountAsync() >= settings.MaxActivePerOwner)
			{
				throw new CliException($"{owner.Label} already has {settings.MaxActivePerOwner} active tokens.");
			}
		}

		private static ApiToken NewToken(Owner owner, string name, string scopes, string? allowedIPs, DateTime expiresAt, out string secret)
		{
			secret = ApiTokenFormat.Generate();
			return new ApiToken
			{
				OwnerUserID = owner.Kind == ApiTokenOwnerKinds.User ? owner.ID : null,
				OwnerServiceID = owner.Kind == ApiTokenOwnerKinds.ServiceAccount ? owner.ID : null,
				Name = name,
				TokenHash = ApiTokenFormat.Hash(secret),
				TokenLast4 = ApiTokenFormat.Last4(secret),
				Scopes = scopes,
				AllowedIPs = String.IsNullOrWhiteSpace(allowedIPs) ? null : allowedIPs,
				CreatedBy = CliActor(),
				CreatedAt = DateTime.UtcNow,
				ExpiresAt = expiresAt
			};
		}

		private static async Task<int> Create(AppDbContext db, ApiTokenSettings settings, Arguments parsed)
		{
			Owner owner = await ResolveOwner(db, parsed.Options.GetValueOrDefault("--owner") ?? throw new CliException("--owner is required."));

			string name = parsed.Options.GetValueOrDefault("--name", String.Empty).Trim();
			if (name.Length == 0 || name.Length > ApiToken.NameMaxLength)
			{
				throw new CliException($"--name is required, at most {ApiToken.NameMaxLength} characters.");
			}

			List<string> scopes = ApiTokenFormat.ParseScopes(parsed.Options.GetValueOrDefault("--scopes", String.Empty));
			if (scopes.Count == 0)
			{
				throw new CliException("--scopes needs at least one permission.");
			}

			scopes.ForEach(s => RequirePermission(s));

			if (owner.Kind == ApiTokenOwnerKinds.User)
			{
				string? machine = scopes.FirstOrDefault(Permissions.IsMachine);
				if (machine != null)
				{
					throw new CliException($"{machine} is a machine permission and only service accounts hold it.");
				}
			}

			HashSet<string> held = await OwnerPermissions(db, owner);
			string? missing = scopes.FirstOrDefault(s => !held.Contains(s));
			if (missing != null)
			{
				throw new CliException($"{owner.Label} does not hold {missing}; a token never grants more than its owner holds.");
			}

			string? allowedIPs = parsed.Options.GetValueOrDefault("--allowed-ips");
			try
			{
				ApiTokenFormat.ParseAllowedIPs(allowedIPs);
			}
			catch (FormatException ex)
			{
				throw new CliException(ex.Message);
			}

			DateTime expiresAt = ParseExpiry(parsed.Options.GetValueOrDefault("--expires") ?? throw new CliException("--expires is required."));
			CheckLifetime(settings, scopes, allowedIPs, expiresAt);
			await CheckActiveLimit(db, settings, owner);

			ApiToken token = NewToken(owner, name, String.Join(' ', scopes), allowedIPs, expiresAt, out string secret);
			db.ApiTokens.Add(token);
			// the row and its audit entry commit together
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				await db.SaveChangesAsync();
				db.AuditLog.Add(Audit("token_mint", AuditTargetKinds.Token, token.ID, new { name, owner = owner.Label, scopes, expires_at = expiresAt, allowed_ips = token.AllowedIPs }));
				await db.SaveChangesAsync();
				await tx.CommitAsync();
			}

			Console.WriteLine($"Token {token.ID} ({name}) for {owner.Label}, expires {expiresAt:yyyy-MM-dd}. It is shown once:");
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
			DateTime now = DateTime.UtcNow;

			if (old.ExpiresAt <= now || old.RevokedAt != null)
			{
				throw new CliException($"Token {old.ID} is revoked, expired or already rotated; rotate its replacement or create a new one.");
			}

			int overlap = 0;
			if (parsed.Options.TryGetValue("--overlap-minutes", out string? overlapText)
				&& (!int.TryParse(overlapText, NumberStyles.None, CultureInfo.InvariantCulture, out overlap) || overlap > MaximumOverlapMinutes))
			{
				throw new CliException($"--overlap-minutes is 0 to {MaximumOverlapMinutes}.");
			}

			Owner owner = old.OwnerServiceID != null
				? new Owner(ApiTokenOwnerKinds.ServiceAccount, old.OwnerServiceID.Value, "service:" + (await db.ServiceAccounts.AsNoTracking().Where(a => a.ID == old.OwnerServiceID).Select(a => a.Name).FirstAsync()))
				: new Owner(ApiTokenOwnerKinds.User, old.OwnerUserID!.Value, "user:" + old.OwnerUserID);

			List<string> scopes = ApiTokenFormat.ParseScopes(old.Scopes);
			DateTime expiresAt = parsed.Options.TryGetValue("--expires", out string? expiresText)
				? ParseExpiry(expiresText)
				: now + (old.ExpiresAt - old.CreatedAt);
			CheckLifetime(settings, scopes, old.AllowedIPs, expiresAt);
			if (overlap > 0)
			{
				await CheckActiveLimit(db, settings, owner);
			}

			ApiToken token = NewToken(owner, old.Name, old.Scopes, old.AllowedIPs, expiresAt, out string secret);
			old.RevokedAt = now.AddMinutes(overlap);
			old.RevokedBy = CliActor();
			db.ApiTokens.Add(token);
			// the row and its audit entry commit together
			await using (var tx = await db.Database.BeginTransactionAsync())
			{
				await db.SaveChangesAsync();
				db.AuditLog.Add(Audit("token_rotate", AuditTargetKinds.Token, token.ID, new { old_token_id = old.ID, overlap_minutes = overlap, expires_at = expiresAt }));
				await db.SaveChangesAsync();
				await tx.CommitAsync();
			}

			Console.WriteLine($"Token {token.ID} replaces {old.ID}, which stops {(overlap == 0 ? "now" : $"in {overlap} minutes")}. The new token is shown once:");
			Console.WriteLine(secret);
			return 0;
		}

		private static async Task<int> Revoke(AppDbContext db, Arguments parsed)
		{
			ApiToken token = await RequireToken(db, parsed.Positional[1]);
			string reason = parsed.Options.GetValueOrDefault("--reason", String.Empty).Trim();
			if (reason.Length > ModerationManager.MaximumReasonLength)
			{
				throw new CliException($"The reason must be {ModerationManager.MaximumReasonLength} characters or fewer.");
			}

			DateTime now = DateTime.UtcNow;
			if (token.RevokedAt != null && token.RevokedAt <= now)
			{
				Console.WriteLine($"Token {token.ID} is already revoked.");
				return 0;
			}

			token.RevokedAt = now;
			token.RevokedBy = CliActor();
			db.AuditLog.Add(Audit("token_revoke", AuditTargetKinds.Token, token.ID, new { reason }, reason));
			await db.SaveChangesAsync();
			Console.WriteLine($"Token {token.ID} is revoked.");
			return 0;
		}

		private static async Task<int> List(AppDbContext db, Arguments parsed)
		{
			IQueryable<ApiToken> query = db.ApiTokens.AsNoTracking();
			if (parsed.Options.TryGetValue("--owner", out string? ownerText))
			{
				Owner owner = await ResolveOwner(db, ownerText);
				query = owner.Kind == ApiTokenOwnerKinds.ServiceAccount ? query.Where(t => t.OwnerServiceID == owner.ID) : query.Where(t => t.OwnerUserID == owner.ID);
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
