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
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace GenOnlineService;

public partial class LobbyManager
{
	internal async Task<RealtimeConnection?> OpenSessionAsync(AppDbContext db, EUserSessionType sessionType, bool reconnect,
		long userID, string clientID, string ipAddress, string continent, string country,
		double latitude, double longitude, HubConnectionContext transport, string? authorizationID)
	{
		using var membership = await AcquireMembershipGateAsync(userID);
		var previous = membershipConnections.GetSession(userID, sessionType);
		if (!reconnect && previous != null) await DestroySessionAsync(previous);
		if (reconnect && (previous == null || previous.IsRetired)) return null;
		var installed = await WebSocketManager.InstallSession(db, sessionType, reconnect, userID, clientID,
			ipAddress, continent, country, latitude, longitude, transport, authorizationID);
		if (reconnect && previous != null) await InvalidateSessionMeshAsync(previous);
		return installed;
	}

	internal async Task DisconnectSessionAsync(UserSession? session, RealtimeConnection? socket, bool destroySession)
	{
		if (session == null) return;
		using (await AcquireMembershipGateAsync(session.m_UserID))
		{
			// Destroying must not depend on which socket is attached: a client reconnecting while a ban/kick/logout is
			// flushing would otherwise keep a session that was meant to die. Only abandoning is bound to the socket instance.
			if (IsCurrentSession(session) && (destroySession || ReferenceEquals(WebSocketManager.GetWebSocketForSession(session), socket)))
			{
				if (destroySession) await DestroySessionAsync(session);
				else
				{
					WebSocketManager.DetachSocket(session);
					await InvalidateSessionMeshAsync(session);
					session.MarkAbandoned();
					var lobby = GetLobby(session.currentLobbyID);
					if (lobby?.State == ELobbyState.INGAME) lobby.RecordPlayerIngameAbandon(session.m_UserID);
				}
			}
			// also reached when a new registration replaces the session, so the old connection must hear why
			if (socket != null) await socket.CloseAsync(CloseCodes.SessionSuperseded, allowReconnect: false);
		}
	}

	internal Task<bool> ClearSessionAsync(UserSession? session) => RemoveSessionAsync(session, expiredOnly: false);
	internal Task<bool> ExpireSessionAsync(UserSession session) => RemoveSessionAsync(session, expiredOnly: true);

	private async Task<bool> RemoveSessionAsync(UserSession? session, bool expiredOnly)
	{
		if (session == null) return false;
		using var membership = await AcquireMembershipGateAsync(session.m_UserID);
		if (!IsCurrentSession(session)) return false;
		if (expiredOnly && !WebSocketManager.ShouldStillClearAbandonedSession(session, session)) return false;
		await DestroySessionAsync(session);
		return true;
	}

	// Stores the user's middleware ID and, only when the stored value changed while they are in a lobby, re-signals
	// the other members. The in-lobby check, the write and the notify decision all happen under the user's membership
	// gate (the one joins and leaves take), then the lobby gate: the documented acquisition order. The lobby callback
	// only enqueues websocket sends and flips dirty flags, so it never re-enters either gate.
	internal async Task<WebSocketManager.EMiddlewareIDApplyResult> ApplyMiddlewareIDAsync(long userID, EUserSessionType sessionType, string middlewareID)
	{
		using var membership = await AcquireMembershipGateAsync(userID);
		var session = membershipConnections.GetSession(userID, sessionType);
		// The client posts its token in parallel with opening the websocket after login, so the session may not exist yet:
		// store the ID anyway (not in a lobby, nothing to notify) so the session that opens next picks it up.
		bool live = session != null && !session.IsRetired;
		bool inLobby = live && session!.currentLobbyID != -1;
		var result = WebSocketManager.ApplyMiddlewareIDForUser(userID, middlewareID, inLobby);
		if (result == WebSocketManager.EMiddlewareIDApplyResult.Changed && inLobby)
		{
			var lobby = GetLobby(session.currentLobbyID);
			if (lobby != null) await lobby.NotifyMemberMiddlewareIDResolved(userID);
		}
		return result;
	}

	private bool IsCurrentSession(UserSession session) =>
		ReferenceEquals(membershipConnections.GetSession(session.m_UserID, session.GetSessionType()), session);

	private async Task DestroySessionAsync(UserSession session)
	{
		session.Retire();
		bool gameplay = SessionHelpers.SessionTypeHasAccessTo(session.GetSessionType(), ESessionAccessType.Gameplay);
		try
		{
			if (gameplay)
			{
				await InvalidateSessionMeshAsync(session);
				await MatchmakingManager.DetachSessionAsync(session);
				await LeaveMembershipsAsync(session.m_UserID);
				await CleanupUserLobbiesNotStartedAsync(session.m_UserID);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Destroying session for user {UserId} failed", session.m_UserID);
			throw;
		}
		finally
		{
			try
			{
				if (gameplay) await membershipConnections.DeleteRelayCredentialsAsync(session.m_UserID);
			}
			finally
			{
				var socket = WebSocketManager.DetachSocket(session);
				try
				{
					WebSocketManager.RemoveSessionData(session);
				}
				finally
				{
					// a destroyed session takes its connection down, callers that need another close code close first
					if (socket != null) await socket.CloseAsync(CloseCodes.SessionSuperseded, allowReconnect: false);
				}
			}
		}
	}

	private async Task InvalidateSessionMeshAsync(UserSession session)
	{
		if (!SessionHelpers.SessionTypeHasAccessTo(session.GetSessionType(), ESessionAccessType.Gameplay)) return;
		var lobby = GetLobby(session.currentLobbyID);
		if (lobby != null) await lobby.InvalidateMeshForSessionAsync(session);
	}

	// Returns the new mesh check id, 0 when the caller may not start one now
	internal async Task<long> StartMeshCheckAsync(UserSession session, RealtimeConnection socket)
	{
		using var membership = await AcquireMembershipGateAsync(session.m_UserID);
		if (!IsCurrentSession(session) || session.IsRetired || !ReferenceEquals(WebSocketManager.GetWebSocketForSession(session), socket)) return 0;
		var lobby = GetLobby(session.currentLobbyID);
		return lobby == null ? 0 : await lobby.StartMeshCheckAsync(session, socket);
	}

	internal async Task StoreMeshResponseAsync(UserSession session, RealtimeConnection socket, MeshCheckResponseRequest response)
	{
		using var membership = await AcquireMembershipGateAsync(session.m_UserID);
		if (!IsCurrentSession(session) || session.IsRetired || !ReferenceEquals(WebSocketManager.GetWebSocketForSession(session), socket)) return;
		var lobby = GetLobby(session.currentLobbyID);
		if (lobby != null) await lobby.StoreMeshResponseAsync(session, socket, response);
	}

	internal async Task CancelMatchmakingAsync(UserSession session)
	{
		using var membership = await AcquireMembershipGateAsync(session.m_UserID);
		if (!IsCurrentSession(session) || session.IsRetired) return;
		var detached = await MatchmakingManager.DetachSessionAsync(session);
		foreach (var entry in detached.Members)
			await RemoveMembershipAsync(entry.Lobby, session.m_UserID, entry.Member);
		if (!detached.CancellationRejected) await LeaveMembershipsAsync(session.m_UserID);
	}
}
