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
	// The Friends snapshot and presence (section 6.5), friend and block changes are REST (SocialActions)
	public sealed partial class RealtimeHub
	{
		private partial async Task SendFriendsSnapshotAsync(UserSession session)
		{
			SocialLists lists;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				lists = await SocialActions.ListsAsync(db, session.m_UserID);
			}

			await Clients.Caller.Friends(new FriendsEvent { Friends = lists.Friends, Incoming = lists.Incoming, Outgoing = lists.Outgoing, Blocked = lists.Blocked });
		}

		private partial Task OnConnectionOpenedAsync(UserSession session)
		{
			PublishPresence(session.m_UserID);
			return Task.CompletedTask;
		}

		private partial Task OnConnectionClosedAsync(UserSession session) => LeaveChannelOnCloseAsync(session);

		// Sends FriendPresence about the user to their online friends
		internal static void PublishPresence(Int64 userID)
		{
			FriendPresenceEvent presence = RealtimePresence.Of(userID);
			IReadOnlyCollection<Int64> friends = FriendsToTell(userID);
			if (friends.Count > 0)
			{
				RealtimeOut.Fire(RealtimeOut.Users(friends).FriendPresence(presence));
			}
		}

		// The user's own cache when they are online, which reaches friends on every copy, otherwise every user on this copy who lists them as a friend
		private static IReadOnlyCollection<Int64> FriendsToTell(Int64 userID)
		{
			if (SocialState.Of(userID) != null)
			{
				return SocialState.Snapshot(userID, c => c.Friends);
			}

			return RealtimeAudience.AllSessions().Select(s => s.m_UserID).Distinct().Where(id => SocialState.HasFriend(id, userID)).ToArray();
		}
	}
}
