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

using GenOnlineService.Realtime;
using System.Collections.Concurrent;

namespace GenOnlineService
{
	public partial class Lobby
	{
		// Only FromLive builds a lobby without a creating session
		private Lobby()
		{
		}
	}

	// The restored lobbies of a copy that took over after an unplanned failover, until their members reconnect or the grace ends
	public partial class LobbyManager
	{
		// The members of restored lobbies whose session has not come back, by user id
		private readonly ConcurrentDictionary<long, long> m_restoredMembers = new();
		private long m_restoredDeadline;

		internal bool HasRestoredMembers => !m_restoredMembers.IsEmpty;

		// Adds a lobby restored from the live state, false when a lobby with that id is already here
		internal bool AdoptRestoredLobby(Lobby lobby, long graceMs)
		{
			if (!m_dictLobbies.TryAdd(lobby.LobbyID, lobby))
			{
				return false;
			}

			lobby.OnLobbyNeedsDestroyed += HandleLobbyNeedsDestroyed;
			foreach (long userID in lobby.MembersWithoutSession())
			{
				// a claim of another restored lobby stays, the takeover already kept only the newest seat
				if (!m_restoredMembers.TryAdd(userID, lobby.LobbyID))
				{
					_logger.LogWarning("User {UserId} of restored lobby {LobbyId} already holds a seat in restored lobby {OtherLobbyId}", userID, lobby.LobbyID, m_restoredMembers.GetValueOrDefault(userID));
				}
			}
			Interlocked.Exchange(ref m_restoredDeadline, Environment.TickCount64 + graceMs);
			return true;
		}

		// A game connection that opened after Ready: a member of a restored lobby takes its seat, a restored ticket is queued again
		internal async Task AttachRestoredAsync(UserSession session)
		{
			if (session.GetSessionType() != EUserSessionType.GameClient || (m_restoredMembers.IsEmpty && !MatchmakingManager.HasRestoredTickets))
			{
				return;
			}

			bool attached = false;
			using (await AcquireMembershipGateAsync(session.m_UserID))
			{
				if (!IsCurrentSession(session) || session.IsRetired)
				{
					return;
				}

				if (session.currentLobbyID == -1 && m_restoredMembers.TryRemove(session.m_UserID, out long lobbyID))
				{
					Lobby? lobby = GetLobby(lobbyID);
					attached = lobby != null && await lobby.AttachRestoredMemberAsync(session);
					if (attached)
					{
						_logger.LogInformation("User {UserId} took its seat again in restored lobby {LobbyId}", session.m_UserID, lobbyID);
					}
				}

				await MatchmakingManager.ReattachRestoredTicketAsync(session);
			}

			if (attached)
			{
				RealtimeHub.PublishPresence(session.m_UserID);
			}
		}

		// After the grace, restored members who never reconnected leave their lobby and unclaimed tickets are dropped
		private async Task DropUnclaimedRestoredMembersAsync()
		{
			if (m_restoredMembers.IsEmpty && !MatchmakingManager.HasRestoredTickets)
			{
				return;
			}
			if (Environment.TickCount64 < Interlocked.Read(ref m_restoredDeadline))
			{
				return;
			}

			MatchmakingManager.DropExpiredRestoredTickets();
			foreach ((long userID, long lobbyID) in m_restoredMembers.ToArray())
			{
				if (!m_restoredMembers.TryRemove(new KeyValuePair<long, long>(userID, lobbyID)))
				{
					continue;
				}

				using (await AcquireMembershipGateAsync(userID))
				{
					Lobby? lobby = GetLobby(lobbyID);
					if (lobby != null && await lobby.DropRestoredMemberAsync(userID))
					{
						_logger.LogInformation("User {UserId} did not reconnect to restored lobby {LobbyId} and left it", userID, lobbyID);
					}
				}
			}
		}
	}
}
