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

		public static async Task<Result<Empty>> SetDisplayNameAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 userID, string? requested)
		{
			string name = requested ?? String.Empty;
			if (name.Any(Char.IsControl) || !String.Equals(TextSanitizer.Clean(name, StoredNameMaxLength), name, StringComparison.Ordinal))
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

			if (!StaffRoles.HasPermission(userID, Permissions.NamesProtected))
			{
				string? word = ReservedWordIn(name);
				if (word != null)
				{
					return Result.Fail(ErrorCodes.NameReserved, new { Word = word });
				}
			}

			string? storedName;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				if (!await Database.Users.Exists(db, userID))
				{
					return Result.NotFound("user");
				}
				storedName = await Database.Users.GetDisplayName(db, userID);
				if (!await Database.Users.SetDisplayName(db, userID, name))
				{
					return Result.Fail(ErrorCodes.NameTaken);
				}
			}

			// a user with no session on this copy only changes the stored name, sessions read it when they open
			SharedUserData? userData = WebSocketManager.GetSharedDataForUser(userID);
			string oldName = userData?.m_strDisplayName ?? storedName ?? String.Empty;
			if (String.Equals(oldName, name, StringComparison.Ordinal))
			{
				return Result.Success();
			}
			if (userData != null)
			{
				userData.m_strDisplayName = name;
			}

			IRealtimeClient audience = RealtimeOut.Groups(NameAudience(userID));
			foreach (UserSession userSession in WebSocketManager.GetAllDataFromUser(userID))
			{
				await audience.MemberUpdate(new MemberUpdateEvent { Member = RealtimeMembers.Of(userSession) });
			}
			await audience.Notice(new NoticeEvent { Code = ErrorCodes.NameChanged, Params = new { UserId = userID, OldName = oldName, NewName = name } });
			return Result.Success();
		}

		// Everyone who can see the user: their own connections, their channels, their lobby and their friends, as disjoint groups so nobody hears it twice
		private static IReadOnlyList<string> NameAudience(Int64 userID)
		{
			List<UserSession> own = WebSocketManager.GetAllDataFromUser(userID);
			HashSet<Int16> channels = own.Where(s => s.networkRoomID >= 0).Select(s => s.networkRoomID).ToHashSet();
			HashSet<Int64> lobbies = own.Where(s => s.currentLobbyID != -1).Select(s => s.currentLobbyID).ToHashSet();

			// the user and their friends hear it on every connection and every copy
			HashSet<Int64> wholeUsers = SocialState.Snapshot(userID, c => c.Friends).ToHashSet();
			wholeUsers.Add(userID);
			HashSet<string> groups = new(wholeUsers.Select(RealtimeGroups.User), StringComparer.Ordinal);

			// channel and lobby members are known from this copy's sessions only, each through its own session group
			foreach (UserSession session in RealtimeAudience.AllSessions())
			{
				bool sees = channels.Contains(session.networkRoomID)
					|| (session.GetSessionType() == EUserSessionType.GameClient && lobbies.Contains(session.currentLobbyID));
				if (sees && !wholeUsers.Contains(session.m_UserID) && RealtimeAudience.ConnectionOf(session) != null)
				{
					groups.Add(RealtimeGroups.UserSession(session.m_UserID, session.GetSessionType()));
				}
			}
			return groups.ToArray();
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
