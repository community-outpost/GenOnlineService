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
	// The rules of a chosen display name, PlayerNames performs the rename
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
			// a name whose normalized form outgrows the stored column is too long for the service to hold
			if (!Database.DisplayNames.IsStorable(name))
			{
				return Result.Fail(ErrorCodes.NameLength, new { Min = RealtimeHub.NameMinLength, Max = RealtimeHub.NameMaxLength });
			}
			return null;
		}

		// The first reserved word in the name, compared case-insensitively
		public static string? ReservedWordIn(string name)
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
