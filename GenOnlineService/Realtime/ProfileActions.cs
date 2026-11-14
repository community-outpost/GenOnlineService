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
using System.Text.RegularExpressions;

namespace GenOnlineService.Realtime
{
	// Display name changes (PATCH /api/v1/me), everyone who can see the user gets MemberUpdate and the name.changed notice
	public static class ProfileActions
	{
		private const int StoredNameMaxLength = 32;

		// Words only staff may use in a display name, badge ids and badge texts are added at check time
		private static readonly string[] s_reservedNameWords =
		{
			"admin", "staff", "mass^", "mas^", "m4ss^", "m4s^", "moderator",
			"hitler", "h1tler", "h1tl3r", "hittler", "h1ttler", "h1ttl3r",
			"olda", "oldanalytics", "ibra", "x64", "ronin"
		};

		// Lobbies add (N) to tell duplicate names apart, so a name never ends with one
		private static readonly Regex s_dedupeSuffix = new(@"\((1[0-9]|20|[0-9])\)$", RegexOptions.Compiled);

		// The rules every chosen name follows (the Keycloak plugin applies the same ones); reserved words are checked apart
		public static Failure? CheckNameRules(string name)
		{
			// '#' separates the name from its tag
			if (name.Contains('#') || name.Any(Char.IsControl) || !String.Equals(TextSanitizer.Clean(name, StoredNameMaxLength), name, StringComparison.Ordinal))
			{
				return Result.Fail(ErrorCodes.NameInvalidCharacters);
			}
			if (name.Length < RealtimeHub.NameMinLength || name.Length > RealtimeHub.NameMaxLength)
			{
				return Result.Fail(ErrorCodes.NameLength, new { Min = RealtimeHub.NameMinLength, Max = RealtimeHub.NameMaxLength });
			}
			if (Char.IsWhiteSpace(name[0]) || Char.IsWhiteSpace(name[^1]))
			{
				return Result.Fail(ErrorCodes.NameWhitespace);
			}
			if (s_dedupeSuffix.IsMatch(name))
			{
				return Result.Fail(ErrorCodes.NameInvalidCharacters);
			}
			return null;
		}

		public static async Task<Result<Empty>> SetDisplayNameAsync(IDbContextFactory<AppDbContext> dbFactory, AccountsSettings accounts, Int64 userID, string? requested)
		{
			// Where Keycloak owns names, players rename there (kc_action=UPDATE_PROFILE) and the change arrives by copy
			if (accounts.NamesFromUpstream)
			{
				return Result.Fail(ErrorCodes.NameManaged);
			}

			string name = requested ?? String.Empty;
			Failure? invalid = CheckNameRules(name);
			if (invalid != null)
			{
				return invalid.Value;
			}

			if (!StaffRoles.HasPermission(userID, Permissions.NamesProtected))
			{
				string? word = ReservedWordIn(name);
				if (word != null)
				{
					return Result.Fail(ErrorCodes.NameReserved, new { Word = word });
				}
			}

			NameChange? change;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				if (!await Database.Users.Exists(db, userID))
				{
					return Result.NotFound("user");
				}
				change = await Database.Users.SetDisplayName(db, userID, name);
			}
			if (change == null)
			{
				// every tag of the name is in use
				return Result.Fail(ErrorCodes.NameTaken);
			}

			if (change.OldName != change.NewName || change.OldTag != change.NewTag)
			{
				await RealtimeNames.PublishOne(change);
			}
			return Result.Success();
		}

		private static string? ReservedWordIn(string name)
		{
			string lower = name.ToLowerInvariant();
			IEnumerable<string> badgeWords = RealtimeBadges.Definitions.SelectMany(b => new[] { b.Key, b.Value.Text });
			foreach (string word in s_reservedNameWords.Concat(badgeWords))
			{
				if (!String.IsNullOrWhiteSpace(word) && lower.Contains(word.ToLowerInvariant(), StringComparison.Ordinal))
				{
					return word;
				}
			}
			return null;
		}
	}
}
