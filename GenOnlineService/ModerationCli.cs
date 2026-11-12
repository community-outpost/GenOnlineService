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

using Database;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GenOnlineService
{
	// "roles" and "bans" change the database directly, a running service applies the change on its next reconcile tick
	public static class ModerationCli
	{
		private const string RolesUsage =
			"Usage:\n"
			+ "  GenOnlineService roles list [<user_id>]\n"
			+ "  GenOnlineService roles grant <user_id> <role> [--reason <text>] [--by <user_id>]\n"
			+ "  GenOnlineService roles revoke <user_id> <role> [--reason <text>] [--by <user_id>]";

		private const string BansUsage =
			"Usage:\n"
			+ "  GenOnlineService bans list <user_id>\n"
			+ "  GenOnlineService bans add <user_id> --reason <text> [--days <n>] [--by <user_id>]\n"
			+ "  GenOnlineService bans revoke <user_id> [--reason <text>] [--by <user_id>]";

		private const int MaximumBanDays = 36500;

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

		private static Int64 ParseUserID(string text)
		{
			if (!Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out Int64 userID) || userID <= 0)
			{
				throw new CliException($"Not a user id: {text}");
			}

			return userID;
		}

		private static string ParseReason(Arguments parsed, bool required)
		{
			string reason = parsed.Options.GetValueOrDefault("--reason", String.Empty).Trim();

			if (required && reason.Length == 0)
			{
				throw new CliException("A reason is required (--reason <text>).");
			}

			if (reason.Length > ModerationManager.MaximumReasonLength)
			{
				throw new CliException($"The reason must be {ModerationManager.MaximumReasonLength} characters or fewer.");
			}

			return reason;
		}

		private static ModerationActor ParseActor(Arguments parsed)
		{
			if (!parsed.Options.TryGetValue("--by", out string? by))
			{
				return new ModerationActor { Name = "cli", Source = ModerationSources.Cli };
			}

			Int64 userID = ParseUserID(by);
			return new ModerationActor { UserID = userID, Name = $"cli:user:{userID}", Source = ModerationSources.Cli };
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

		private static async Task RequireUser(AppDbContext db, Int64 userID)
		{
			if (!await db.Users.AsNoTracking().AnyAsync(u => u.ID == userID))
			{
				throw new CliException($"User {userID} does not exist.");
			}
		}

		private static async Task<int> Run(Func<Task<int>> command)
		{
			try
			{
				return await command();
			}
			catch (CliException ex)
			{
				Console.Error.WriteLine(ex.Message);
				return 1;
			}
		}

		public static Task<int> RunRoles(string[] args) => Run(async () =>
		{
			Arguments parsed = Parse(args, RolesUsage, "--reason", "--by");
			string verb = parsed.Positional.FirstOrDefault()?.ToLowerInvariant() ?? String.Empty;

			if (verb == "list" && parsed.Positional.Count <= 2)
			{
				await using AppDbContext db = await OpenDatabase();
				List<UserRole> rows = parsed.Positional.Count == 2
					? await Database.Moderation.GetRoles(db, ParseUserID(parsed.Positional[1]))
					: (await Database.Moderation.GetAllRoles(db)).OrderBy(r => r.UserID).ThenBy(r => r.Role).ToList();

				if (rows.Count == 0)
				{
					Console.WriteLine("No staff roles.");
				}

				foreach (UserRole r in rows)
				{
					Console.WriteLine($"user {r.UserID}  {r.Role}  {AuthorityScopeColumn.ToText(r.Scope)}  granted {r.GrantedAt:yyyy-MM-dd HH:mm} UTC by {(r.GrantedBy?.ToString(CultureInfo.InvariantCulture) ?? "cli")}  {r.Reason}");
				}

				return 0;
			}

			if ((verb == "grant" || verb == "revoke") && parsed.Positional.Count == 3)
			{
				Int64 userID = ParseUserID(parsed.Positional[1]);
				string role = parsed.Positional[2].ToLowerInvariant();

				if (!StaffRoles.IsKnown(role))
				{
					throw new CliException($"Unknown role {role}. Known roles: {string.Join(", ", StaffRoles.All)}.");
				}

				string reason = ParseReason(parsed, required: false);
				ModerationActor actor = ParseActor(parsed);

				await using AppDbContext db = await OpenDatabase();
				await RequireUser(db, userID);

				if (verb == "grant")
				{
					bool granted = await Database.Moderation.GrantRole(db, userID, role, EAuthorityScope.Local, reason, actor);
					Console.WriteLine(granted ? $"Granted {role} to user {userID}." : $"User {userID} already has {role}, nothing changed.");
				}
				else
				{
					bool revoked = await Database.Moderation.RevokeRole(db, userID, role, EAuthorityScope.Local, reason, actor);
					Console.WriteLine(revoked ? $"Revoked {role} from user {userID}." : $"User {userID} does not have {role}, nothing changed.");
				}

				return 0;
			}

			throw new CliException(RolesUsage);
		});

		public static Task<int> RunBans(string[] args) => Run(async () =>
		{
			Arguments parsed = Parse(args, BansUsage, "--reason", "--days", "--by");
			string verb = parsed.Positional.FirstOrDefault()?.ToLowerInvariant() ?? String.Empty;

			if (parsed.Positional.Count != 2 || (verb != "list" && verb != "add" && verb != "revoke"))
			{
				throw new CliException(BansUsage);
			}

			Int64 userID = ParseUserID(parsed.Positional[1]);

			if (verb == "list")
			{
				await using AppDbContext db = await OpenDatabase();
				await RequireUser(db, userID);
				DateTime now = DateTime.UtcNow;

				if (await db.Users.AsNoTracking().AnyAsync(u => u.ID == userID && u.IsBanned != false))
				{
					Console.WriteLine($"users.banned is set for user {userID}, which bans them regardless of the rows below.");
				}

				List<Ban> bans = await Database.Moderation.GetBans(db, userID);
				if (bans.Count == 0)
				{
					Console.WriteLine("No bans.");
				}

				foreach (Ban b in bans)
				{
					string state = b.RevokedAt != null ? $"revoked {b.RevokedAt:yyyy-MM-dd HH:mm} UTC ({b.RevokeReason})"
						: b.ExpiresAt != null && b.ExpiresAt <= now ? "expired"
						: "active";
					string until = b.ExpiresAt == null ? "permanent" : $"until {b.ExpiresAt:yyyy-MM-dd HH:mm} UTC";
					Console.WriteLine($"#{b.ID}  {AuthorityScopeColumn.ToText(b.Scope)}  {b.CreatedAt:yyyy-MM-dd HH:mm} UTC  {until}  {state}  {b.Reason}");
				}

				return 0;
			}

			ModerationActor actor = ParseActor(parsed);

			if (verb == "add")
			{
				string reason = ParseReason(parsed, required: true);
				DateTime? expiresAt = null;

				if (parsed.Options.TryGetValue("--days", out string? daysText))
				{
					if (!int.TryParse(daysText, NumberStyles.None, CultureInfo.InvariantCulture, out int days) || days <= 0 || days > MaximumBanDays)
					{
						throw new CliException($"--days must be a whole number from 1 to {MaximumBanDays}, got {daysText}. Leave it out for a permanent ban.");
					}

					expiresAt = DateTime.UtcNow.AddDays(days);
				}

				await using AppDbContext db = await OpenDatabase();
				await RequireUser(db, userID);
				Ban ban = await Database.Moderation.AddBan(db, userID, EAuthorityScope.Local, reason, expiresAt, actor);
				Console.WriteLine(expiresAt == null ? $"Banned user {userID} permanently (ban #{ban.ID})." : $"Banned user {userID} until {expiresAt:yyyy-MM-dd HH:mm} UTC (ban #{ban.ID}).");
				return 0;
			}

			{
				string reason = ParseReason(parsed, required: false);
				await using AppDbContext db = await OpenDatabase();
				await RequireUser(db, userID);
				int revoked = await Database.Moderation.RevokeActiveBans(db, userID, EAuthorityScope.Local, reason, actor);
				Console.WriteLine(revoked > 0 ? $"Revoked {revoked} active ban(s) for user {userID}." : $"User {userID} has no active ban, nothing changed.");

				if (await db.Users.AsNoTracking().AnyAsync(u => u.ID == userID && u.IsBanned != false))
				{
					Console.WriteLine($"users.banned is still set for user {userID}, so they stay banned until it is cleared.");
				}

				return 0;
			}
		});
	}
}
