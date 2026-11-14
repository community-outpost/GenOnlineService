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

using System.Runtime.CompilerServices;

namespace GenOnlineService.Realtime
{
	// Connection sets built from the in-memory sessions, used to address events about one user
	internal static class RealtimeAudience
	{
		public static IEnumerable<UserSession> AllSessions() => WebSocketManager.GetUserDataCache().Values.SelectMany(byUser => byUser.Values);

		public static string? ConnectionOf(UserSession session) => WebSocketManager.GetWebSocketForSession(session)?.ConnectionId;

		// Live connection ids of every session of the user
		public static List<string> ConnectionsOf(Int64 userID)
		{
			List<string> result = new();
			foreach (UserSession session in WebSocketManager.GetAllDataFromUser(userID))
			{
				string? connectionID = ConnectionOf(session);
				if (connectionID != null)
				{
					result.Add(connectionID);
				}
			}
			return result;
		}

		// True when either user blocked the other, read from the online users only
		public static bool BlockedEitherWay(Int64 userA, Int64 userB)
		{
			return SocialState.HasBlocked(userA, userB) || SocialState.HasBlocked(userB, userA);
		}
	}

	// Reads and writes the cached social sets of online users under one lock per user
	internal static class SocialState
	{
		public static UserSocialContainer? Of(Int64 userID) => WebSocketManager.GetSharedDataForUser(userID)?.GetSocialContainer();

		public static bool HasBlocked(Int64 userID, Int64 otherID) => Read(userID, c => c.Blocked.Contains(otherID));
		public static bool HasFriend(Int64 userID, Int64 otherID) => Read(userID, c => c.Friends.Contains(otherID));
		public static bool HasIncoming(Int64 userID, Int64 fromID) => Read(userID, c => c.PendingRequests.Contains(fromID));

		public static bool Read(Int64 userID, Func<UserSocialContainer, bool> read)
		{
			UserSocialContainer? container = Of(userID);
			if (container == null)
			{
				return false;
			}

			lock (container)
			{
				return read(container);
			}
		}

		public static void Update(Int64 userID, Action<UserSocialContainer> update)
		{
			UserSocialContainer? container = Of(userID);
			if (container == null)
			{
				return;
			}

			lock (container)
			{
				update(container);
			}
		}

		public static long[] Snapshot(Int64 userID, Func<UserSocialContainer, HashSet<Int64>> set)
		{
			UserSocialContainer? container = Of(userID);
			if (container == null)
			{
				return Array.Empty<long>();
			}

			lock (container)
			{
				return set(container).ToArray();
			}
		}
	}

	// Presence of a user from their sessions, an abandoned session in its reconnect grace still counts
	internal static class RealtimePresence
	{
		public static FriendPresenceEvent Of(Int64 userID)
		{
			List<UserSession> sessions = WebSocketManager.GetAllDataFromUser(userID);
			PresenceActivity activity = PresenceActivity.Offline;
			Int64? channelID = null;
			foreach (UserSession session in sessions)
			{
				PresenceActivity sessionActivity = ActivityOf(session);
				if (Rank(sessionActivity) > Rank(activity))
				{
					activity = sessionActivity;
				}
				// the game client's channel wins when the sessions sit in different channels
				if (session.networkRoomID >= 0 && (channelID == null || session.GetSessionType() == EUserSessionType.GameClient))
				{
					channelID = session.networkRoomID;
				}
			}

			return new FriendPresenceEvent
			{
				UserId = userID,
				Online = sessions.Count > 0,
				Clients = sessions.Select(s => RealtimeMembers.ClientOf(s.GetSessionType())).Distinct().OrderBy(c => c).ToArray(),
				Activity = activity,
				ChannelId = channelID
			};
		}

		private static PresenceActivity ActivityOf(UserSession session)
		{
			if (session.currentLobbyID != -1)
			{
				Lobby? lobby = ServiceLocator.Services.GetRequiredService<LobbyManager>().GetLobby(session.currentLobbyID);
				return lobby?.State == ELobbyState.INGAME ? PresenceActivity.InGame : PresenceActivity.Lobby;
			}

			if (session.IsRegisteredForMatchmaking)
			{
				return PresenceActivity.Matchmaking;
			}

			return session.networkRoomID >= 0 ? PresenceActivity.Channel : PresenceActivity.Menu;
		}

		private static int Rank(PresenceActivity activity) => activity switch
		{
			PresenceActivity.InGame => 5,
			PresenceActivity.Lobby => 4,
			PresenceActivity.Matchmaking => 3,
			PresenceActivity.Channel => 2,
			PresenceActivity.Menu => 1,
			_ => 0
		};
	}

	// Sliding window of ChatRateMessages per ChatRateWindowMs, kept as long as the user's shared data lives
	internal static class ChatRateLimiter
	{
		private static readonly ConditionalWeakTable<SharedUserData, Queue<long>> s_windows = new();

		public static bool TryConsume(SharedUserData user, out long retryAfterMs)
		{
			Queue<long> window = s_windows.GetValue(user, _ => new Queue<long>());
			long now = Environment.TickCount64;

			lock (window)
			{
				while (window.TryPeek(out long sent) && now - sent >= RealtimeHub.ChatRateWindowMs)
				{
					window.Dequeue();
				}

				if (window.Count >= RealtimeHub.ChatRateMessages)
				{
					retryAfterMs = Math.Max(1, RealtimeHub.ChatRateWindowMs - (now - window.Peek()));
					return false;
				}

				window.Enqueue(now);
				retryAfterMs = 0;
				return true;
			}
		}
	}
}
