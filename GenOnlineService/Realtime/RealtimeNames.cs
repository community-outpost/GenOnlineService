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
	// Tells everyone who can see a user that the user's name or tag changed: a player's own rename, a copy from
	// Keycloak at sign-in or through the group sync, or a staff rename
	public static class RealtimeNames
	{
		private static readonly ILogger s_log = AppLog.For(typeof(RealtimeNames));

		// Connections that show the user: the user's own, friends, and the user's rooms and lobbies
		public static IReadOnlyList<string> Audience(Int64 userID)
		{
			List<UserSession> own = WebSocketManager.GetAllDataFromUser(userID);
			HashSet<Int16> rooms = own.Where(s => s.networkRoomID >= 0).Select(s => s.networkRoomID).ToHashSet();
			HashSet<Int64> lobbies = own.Where(s => s.currentLobbyID != -1).Select(s => s.currentLobbyID).ToHashSet();
			HashSet<Int64> friends = SocialState.Snapshot(userID, c => c.Friends).ToHashSet();

			HashSet<string> connections = new(StringComparer.Ordinal);
			foreach (UserSession session in RealtimeAudience.AllSessions())
			{
				bool sees = session.m_UserID == userID
					|| rooms.Contains(session.networkRoomID)
					|| (session.GetSessionType() == EUserSessionType.GameClient && lobbies.Contains(session.currentLobbyID))
					|| friends.Contains(session.m_UserID);
				string? connectionID = sees ? RealtimeAudience.ConnectionOf(session) : null;
				if (connectionID != null)
				{
					connections.Add(connectionID);
				}
			}
			return connections.ToArray();
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
			SharedUserData? online = WebSocketManager.GetSharedDataForUser(change.UserID);
			if (online == null)
			{
				// offline: the next session reads the new name from the database
				return;
			}

			online.m_strDisplayName = change.NewName;
			online.m_NameTag = change.NewTag;

			try
			{
				IRealtimeClient audience = RealtimeOut.Connections(Audience(change.UserID));
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
