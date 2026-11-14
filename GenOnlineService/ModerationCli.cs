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
	// "reports" and "messages" are how staff read chat: through a report or an audited lookup, each written to audit_log
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

		private const string ReportsUsage =
			"Usage:\n"
			+ "  GenOnlineService reports list\n"
			+ "  GenOnlineService reports show <report_id> --by <user_id>\n"
			+ "  GenOnlineService reports close <report_id> [--reason <text>] [--by <user_id>]";

		private const string MessagesUsage =
			"Usage:\n"
			+ "  GenOnlineService messages show <message_id> --reason <text> --by <user_id>\n"
			+ "  GenOnlineService messages purge";

		private const int MaximumBanDays = 36500;
		private const int ReportListLimit = 200;

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
				return new ModerationActor { Kind = AuditActorKinds.Cli, Name = "cli", Source = ModerationSources.Cli };
			}

			Int64 userID = ParseUserID(by);
			return new ModerationActor { Kind = AuditActorKinds.Cli, UserID = userID, Name = $"cli:user:{userID}", Source = ModerationSources.Cli };
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
					Console.WriteLine($"user {r.UserID}  {r.Role}  {r.Source}  granted {r.GrantedAt:yyyy-MM-dd HH:mm} UTC by {r.GrantedBy ?? "unknown"}  {r.Reason}");
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
					bool granted = await Database.Moderation.GrantRole(db, userID, role, AuthoritySources.Local, reason, actor);
					Console.WriteLine(granted ? $"Granted {role} to user {userID}." : $"User {userID} already has {role}, nothing changed.");
				}
				else
				{
					bool revoked = await Database.Moderation.RevokeRole(db, userID, role, AuthoritySources.Local, reason, actor);
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
					Console.WriteLine($"#{b.ID}  {b.Restriction}  {b.Source}  {b.CreatedAt:yyyy-MM-dd HH:mm} UTC by {b.CreatedBy ?? "unknown"}  {until}  {state}  {b.Reason}");
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
				Ban ban = await Database.Moderation.AddBan(db, userID, BanRestrictions.Play, reason, expiresAt, actor);
				Console.WriteLine(expiresAt == null ? $"Banned user {userID} permanently (ban #{ban.ID})." : $"Banned user {userID} until {expiresAt:yyyy-MM-dd HH:mm} UTC (ban #{ban.ID}).");
				return 0;
			}

			{
				string reason = ParseReason(parsed, required: false);
				await using AppDbContext db = await OpenDatabase();
				await RequireUser(db, userID);
				int revoked = await Database.Moderation.RevokeActiveBans(db, userID, BanRestrictions.Play, reason, actor);
				Console.WriteLine(revoked > 0 ? $"Revoked {revoked} active ban(s) for user {userID}." : $"User {userID} has no active ban, nothing changed.");

				return 0;
			}
		});

		private static Int64 ParseID(string text, string what)
		{
			if (!Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out Int64 id) || id <= 0)
			{
				throw new CliException($"Not a {what} id: {text}");
			}

			return id;
		}

		// Anything that prints chat text names the staff account that reads it, never the anonymous cli actor
		private static ModerationActor ParseReader(Arguments parsed)
		{
			if (!parsed.Options.ContainsKey("--by"))
			{
				throw new CliException("Reading chat names the staff account that reads it (--by <user_id>).");
			}

			return ParseActor(parsed);
		}

		private static string Describe(ChatMessage m)
		{
			string where = m.Kind switch
			{
				ChatMessageKinds.Channel => $"channel {m.ChannelID}",
				ChatMessageKinds.Lobby => $"lobby {m.LobbyID} match {m.MatchID?.ToString(CultureInfo.InvariantCulture) ?? "none"}",
				_ => $"direct to user {m.RecipientID}"
			};
			string content = m.DeletedAt != null ? $"[deleted {m.DeletedAt:yyyy-MM-dd HH:mm} UTC] {m.Content}" : m.Content;
			return $"#{m.ID}  {m.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC  user {m.SenderID}  {where}  {m.Type}: {content}";
		}

		public static Task<int> RunReports(string[] args) => Run(async () =>
		{
			Arguments parsed = Parse(args, ReportsUsage, "--reason", "--by");
			string verb = parsed.Positional.FirstOrDefault()?.ToLowerInvariant() ?? String.Empty;

			if (verb == "list" && parsed.Positional.Count == 1)
			{
				await using AppDbContext db = await OpenDatabase();
				List<ChatReport> reports = await Database.Chat.GetReports(db, ChatReportStatuses.Open, ReportListLimit);
				if (reports.Count == 0)
				{
					Console.WriteLine("No open reports.");
				}

				foreach (ChatReport r in reports)
				{
					Console.WriteLine($"report {r.ID}  message {r.MessageID}  by user {r.ReporterID}  {r.CreatedAt:yyyy-MM-dd HH:mm} UTC  {r.Reason}");
				}

				return 0;
			}

			if (verb == "show" && parsed.Positional.Count == 2)
			{
				Int64 reportID = ParseID(parsed.Positional[1], "report");
				ModerationActor actor = ParseReader(parsed);

				await using AppDbContext db = await OpenDatabase();
				await RequireUser(db, actor.UserID!.Value);
				ChatReport report = await Database.Chat.GetReport(db, reportID) ?? throw new CliException($"Report {reportID} does not exist.");

				// reading a report shows chat, so every read is logged before anything is printed
				await Database.Moderation.AddAuditEntry(db, actor, ModerationActions.ReportView, AuditTargetKinds.Report, reportID, null, new { message_id = report.MessageID });

				string closed = report.ClosedAt == null ? String.Empty : $" {report.ClosedAt:yyyy-MM-dd HH:mm} UTC";
				Console.WriteLine($"report {report.ID}  {report.Status}{closed}  message {report.MessageID}  by user {report.ReporterID}  {report.CreatedAt:yyyy-MM-dd HH:mm} UTC  {report.Reason}");
				Console.WriteLine("snapshot:");
				Console.WriteLine(report.Snapshot);

				if (Database.Chat.SnapshotAfterPending(report.Snapshot))
				{
					ChatMessage? reported = await Database.Chat.GetMessage(db, report.MessageID);
					List<ChatMessage> after = reported == null ? new List<ChatMessage>() : await Database.Chat.GetMessagesAfter(db, reported);
					Console.WriteLine("after (live, not yet frozen, the retention purge freezes it before the reported message expires):");
					if (after.Count == 0)
					{
						Console.WriteLine("  none");
					}

					foreach (ChatMessage m in after)
					{
						Console.WriteLine("  " + Describe(m));
					}
				}

				return 0;
			}

			if (verb == "close" && parsed.Positional.Count == 2)
			{
				Int64 reportID = ParseID(parsed.Positional[1], "report");
				string reason = ParseReason(parsed, required: false);
				ModerationActor actor = ParseActor(parsed);

				await using AppDbContext db = await OpenDatabase();
				if (await Database.Chat.GetReport(db, reportID) == null)
				{
					throw new CliException($"Report {reportID} does not exist.");
				}

				if (!await Database.Chat.CloseReport(db, reportID))
				{
					Console.WriteLine($"Report {reportID} is already closed, nothing changed.");
					return 0;
				}

				await Database.Moderation.LogAction(db, actor, ModerationActions.ReportClose, AuditTargetKinds.Report, reportID, reason);
				Console.WriteLine($"Closed report {reportID}.");
				return 0;
			}

			throw new CliException(ReportsUsage);
		});

		public static Task<int> RunMessages(string[] args) => Run(async () =>
		{
			Arguments parsed = Parse(args, MessagesUsage, "--reason", "--by");
			string verb = parsed.Positional.FirstOrDefault()?.ToLowerInvariant() ?? String.Empty;

			if (verb == "show" && parsed.Positional.Count == 2)
			{
				Int64 messageID = ParseID(parsed.Positional[1], "message");
				string reason = ParseReason(parsed, required: true);
				ModerationActor actor = ParseReader(parsed);

				await using AppDbContext db = await OpenDatabase();
				await RequireUser(db, actor.UserID!.Value);
				ChatMessage message = await Database.Chat.GetMessage(db, messageID) ?? throw new CliException($"Message {messageID} does not exist.");

				// the audit row is written first and a failed write stops the lookup
				await Database.Moderation.AddAuditEntry(db, actor, ModerationActions.MessageLookup, AuditTargetKinds.Message, messageID, reason, new { kind = message.Kind });
				Console.WriteLine(Describe(message));
				return 0;
			}

			if (verb == "purge" && parsed.Positional.Count == 1)
			{
				await using AppDbContext db = await OpenDatabase();
				ChatPurgeCounts counts = await Database.Chat.PurgeExpired(db, DateTime.UtcNow);
				Console.WriteLine($"Purged {counts.Messages} message(s), {counts.ReadMarkers} read marker(s) and {counts.Reports} closed report(s).");
				return 0;
			}

			throw new CliException(MessagesUsage);
		});
	}
}
