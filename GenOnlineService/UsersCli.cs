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
using System.Globalization;

namespace GenOnlineService
{
	// "users" gets an owner back into an email + password account when there is no mail for a self-service reset
	public static class UsersCli
	{
		private const string Usage =
			"Usage:\n"
			+ "  GenOnlineService users password-reset <user_id> [--password-stdin] [--reason <text>]\n"
			+ "  GenOnlineService users two-factor-reset <user_id> [--reason <text>]\n"
			+ "  GenOnlineService users name-reset <user_id> [--reason <text>]\n"
			+ "password-reset prints a new random password once, or sets the one read from standard input with --password-stdin.\n"
			+ "Both end the account's lockout, revoke every sign-in of the game with its refresh tokens, and sign it out of the website within a minute.\n"
			+ "name-reset replaces an inappropriate player name with a placeholder and ends every sign-in; the player chooses a new name when signing in again.";

		private sealed class CliException : Exception
		{
			public CliException(string message) : base(message) { }
		}

		// Each context the name reset asks for, opened from the configuration like the CLI's own
		private sealed class ConfiguredDbContextFactory : IDbContextFactory<AppDbContext>
		{
			public AppDbContext CreateDbContext() => Program.OpenConfiguredDbContext();
		}

		private sealed record Arguments(string Command, Int64 UserID, bool PasswordFromStdin, string? Reason);

		private static Arguments Parse(string[] args)
		{
			if (args.Length < 3 || !Int64.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out Int64 userID))
			{
				throw new CliException(Usage);
			}

			string command = args[1].ToLowerInvariant();
			if (command != "password-reset" && command != "two-factor-reset" && command != "name-reset")
			{
				throw new CliException(Usage);
			}

			bool fromStdin = false;
			string? reason = null;
			for (int i = 3; i < args.Length; i++)
			{
				if (args[i].Equals("--password-stdin", StringComparison.OrdinalIgnoreCase) && command == "password-reset")
				{
					fromStdin = true;
				}
				else if (args[i].Equals("--reason", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					reason = args[++i];
				}
				else
				{
					// no --password: a password on the command line ends up in shell history and process lists
					throw new CliException($"Unknown option or missing value: {args[i]}{Environment.NewLine}{Usage}");
				}
			}

			return new Arguments(command, userID, fromStdin, reason);
		}

		private static string CliActor()
		{
			string actor = "cli:" + Environment.UserName;
			return actor.Length > 64 ? actor.Substring(0, 64) : actor;
		}

		public static async Task<int> RunUsers(string[] args)
		{
			try
			{
				Arguments parsed = Parse(args);

				await using AppDbContext db = Program.OpenConfiguredDbContext();
				if ((await db.Database.GetPendingMigrationsAsync()).Any())
				{
					throw new CliException("The database has pending migrations. Run \"GenOnlineService migrate\" first.");
				}

				if (parsed.Command == "name-reset")
				{
					var actor = new Database.ModerationActor { Kind = AuditActorKinds.Cli, Name = CliActor(), Source = ModerationSources.Cli };
					Realtime.Result<Realtime.Empty> reset = await new Names.PlayerNames(new ConfiguredDbContextFactory()).ResetNameByStaffAsync(actor, parsed.UserID, parsed.Reason, live: false);
					if (!reset.Ok)
					{
						throw new InvalidOperationException(reset.Code == Realtime.ErrorCodes.NotFound ? $"No user with id {parsed.UserID}." : $"The name of user {parsed.UserID} was not reset: {reset.Code}.");
					}
					Console.WriteLine($"The name of user {parsed.UserID} is reset and their sign-ins ended; they choose a new name when they sign in again.");
					return 0;
				}

				if (parsed.Command == "two-factor-reset")
				{
					await UserResets.ResetTwoFactor(db, parsed.UserID, CliActor(), parsed.Reason);
					Console.WriteLine($"Two-factor sign-in of user {parsed.UserID} is off; they can set it up again after signing in.");
					return 0;
				}

				string? password = parsed.PasswordFromStdin ? Console.In.ReadLine() : null;
				int minLength = Program.g_Config == null ? PasswordSignInSettings.MinimumLength : AccountsSettings.Read(Program.g_Config).Password.MinLength;
				string set = await UserResets.ResetPassword(db, parsed.UserID, password, minLength, CliActor(), parsed.Reason);
				Console.WriteLine(parsed.PasswordFromStdin
					? $"The password of user {parsed.UserID} is set."
					: $"The new password of user {parsed.UserID} (shown once): {set}");
				return 0;
			}
			catch (CliException ex)
			{
				Console.Error.WriteLine(ex.Message);
				return 2;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex.Message);
				return 1;
			}
		}
	}
}
