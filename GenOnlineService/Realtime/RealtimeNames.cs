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

namespace GenOnlineService.Realtime
{
	// Tells everyone who can see a user that the user's name or tag changed: a player's own rename or a staff rename
	public static class RealtimeNames
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RealtimeNames));

		// Everyone who can see the user: their own connections, their channels, their lobby and their friends, as disjoint groups so nobody hears it twice
		public static IReadOnlyList<string> Audience(Int64 userID)
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

		// Updates the online copy of each changed user and sends MemberUpdate, plus name.changed when the name itself changed
		public static void Publish(IEnumerable<NameChange> changes)
		{
			foreach (NameChange change in changes)
			{
				RealtimeOut.Fire(PublishOne(change));
			}
		}

		public static async Task PublishOne(NameChange change)
		{
			// a user with no session on this copy only changes the stored name, sessions read it when they open
			SharedUserData? online = WebSocketManager.GetSharedDataForUser(change.UserID);
			if (online != null)
			{
				online.m_strDisplayName = change.NewName;
				online.m_NameTag = change.NewTag;
			}

			try
			{
				IRealtimeClient audience = RealtimeOut.Groups(Audience(change.UserID));
				foreach (UserSession session in WebSocketManager.GetAllDataFromUser(change.UserID))
				{
					await audience.MemberUpdate(new MemberUpdateEvent { Member = RealtimeMembers.Of(session) });
				}

				if (change.NameChanged)
				{
					await audience.Notice(new NoticeEvent { Code = ErrorCodes.NameChanged, Params = new { UserId = change.UserID, OldName = change.OldName, NewName = change.NewName } });
				}
			}
			catch (Exception ex)
			{
				s_log.LogWarning(ex, "Could not announce the new name of user {UserId}", change.UserID);
			}
		}
	}
}
