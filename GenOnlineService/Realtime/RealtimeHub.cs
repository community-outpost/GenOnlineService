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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using System.Security.Claims;

namespace GenOnlineService.Realtime
{
	// Realtime protocol v1 at /-/realtime; each area of the protocol lives in its own partial file
	[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
	public sealed partial class RealtimeHub : Hub<IRealtimeClient>
	{
		public const string Path = "/-/realtime";
		public const int ProtocolVersion = 1;
		public const int ChatMaxLength = 512;
		public const int ChatRateMessages = 3;
		public const long ChatRateWindowMs = 9000;
		public const int NameMinLength = 3;
		public const int NameMaxLength = 16;

		private const string ConnectionItem = "realtime.connection";

		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly LobbyManager m_lobbyManager;
		private readonly IAuthorizationService m_authorization;
		private readonly IOpenIddictAuthorizationManager m_signIns;
		private readonly ILogger<RealtimeHub> m_log;

		public RealtimeHub(IDbContextFactory<AppDbContext> dbFactory, LobbyManager lobbyManager, IAuthorizationService authorization, IOpenIddictAuthorizationManager signIns, ILogger<RealtimeHub> log)
		{
			m_dbFactory = dbFactory;
			m_lobbyManager = lobbyManager;
			m_authorization = authorization;
			m_signIns = signIns;
			m_log = log;
		}

		public override async Task OnConnectedAsync()
		{
			ClaimsPrincipal user = Context.User!;
			Int64 userID = RealtimeClaims.UserID(user);
			EUserSessionType sessionType = RealtimeClaims.SessionType(user);
			KnownClients.EKnownClients clientID = RealtimeClaims.ClientID(user);
			HubConnectionContext? transport = RealtimeLifetimeManager.Get(Context.ConnectionId);
			if (userID <= 0 || sessionType == EUserSessionType.None || clientID == KnownClients.EKnownClients.unknown || transport == null)
			{
				Context.Abort();
				return;
			}

			string ipAddress = IPHelpers.NormalizeIP(Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString());
			GeoLocation geo = GeoLocation.Lookup(ipAddress);

			// a live session of the same type is taken over, its old connection closes with session.superseded
			UserSession? existing = WebSocketManager.GetSessionFromUser(userID, sessionType);
			bool takeOver = existing != null && !existing.IsRetired;

			RealtimeConnection? connection;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				connection = await WebSocketManager.CreateSession(db, sessionType, takeOver, userID, clientID, ipAddress, geo.Continent, geo.Country, geo.Latitude, geo.Longitude, transport);
			}

			if (connection == null)
			{
				// the session vanished while connecting, a fresh connect builds a new one
				await RealtimeConnectionClose(transport, CloseCodes.ServerRestarting, allowReconnect: true);
				return;
			}

			Context.Items[ConnectionItem] = connection;
			UserSession session = connection.OwnerSession;

			// a logout that ran while this connection was opening found no session to close, so the sign-in is checked again once installed
			if (!await IsSignInStillValidAsync(user, userID, sessionType))
			{
				// only this connection closes, a newer sign-in that already took the session over keeps it
				await connection.CloseAsync(CloseCodes.AuthRevoked, allowReconnect: false);
				if (ReferenceEquals(WebSocketManager.GetWebSocketForSession(session), connection))
				{
					await WebSocketManager.DeleteSession(userID, sessionType, connection, true, session);
				}
				return;
			}

			await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.User(userID));
			if (sessionType == EUserSessionType.GameClient)
			{
				await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeGroups.UserGame(userID));
			}

			await Clients.Caller.Ready(BuildReady(session));
			await SendFriendsSnapshotAsync(session);
			await RestoreRoomAsync(session);
			await RestoreLobbyAsync(session);
			await OnConnectionOpenedAsync(session);
		}

		public override async Task OnDisconnectedAsync(Exception? exception)
		{
			if (Context.Items.TryGetValue(ConnectionItem, out object? item) && item is RealtimeConnection connection)
			{
				UserSession session = connection.OwnerSession;
				LobbyListPublisher.Unwatch(Context.ConnectionId);
				await WebSocketManager.DeleteSession(connection.m_UserID, connection.m_SessionType, connection, false, session);
				await OnConnectionClosedAsync(session);
			}

			await base.OnDisconnectedAsync(exception);
		}

		private async Task<bool> IsSignInStillValidAsync(ClaimsPrincipal user, Int64 userID, EUserSessionType sessionType)
		{
			string? authorizationID = RealtimeClaims.AuthorizationID(user);
			if (sessionType == EUserSessionType.GameClient)
			{
				GameSessionRegistration? registration = GameSessions.Get(userID, sessionType);
				if (registration == null || registration.AuthorizationID != authorizationID)
				{
					return false;
				}
			}

			if (String.IsNullOrEmpty(authorizationID))
			{
				return false;
			}

			object? authorization = await m_signIns.FindByIdAsync(authorizationID);
			return authorization != null && await m_signIns.HasStatusAsync(authorization, OpenIddictConstants.Statuses.Valid);
		}

		private static ReadyEvent BuildReady(UserSession session)
		{
			Int64 userID = session.m_UserID;
			return new ReadyEvent
			{
				ProtocolVersion = ProtocolVersion,
				SessionType = RealtimeMembers.ClientOf(session.GetSessionType()),
				User = RealtimeMembers.Of(userID),
				Permissions = StaffRoles.PermissionsOf(userID).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
				Badges = RealtimeBadges.Definitions,
				Rooms = RoomCatalog.Rooms.Select(room => new RoomInfo
				{
					Id = room.ID,
					Name = room.Name,
					ParentId = room.ParentID,
					ShowAllMatches = room.ID == 0
				}).ToArray(),
				Limits = new Limits
				{
					ChatMaxLength = ChatMaxLength,
					ChatRate = new ChatRate { Messages = ChatRateMessages, WindowMs = ChatRateWindowMs },
					NameMinLength = NameMinLength,
					NameMaxLength = NameMaxLength
				}
			};
		}

		private static async Task RealtimeConnectionClose(HubConnectionContext transport, string code, bool allowReconnect)
		{
			try
			{
				await transport.WriteAsync(new Microsoft.AspNetCore.SignalR.Protocol.CloseMessage(code, allowReconnect));
			}
			catch (Exception)
			{
			}
			transport.Abort();
		}

		// The caller's connection; missing only when a method arrives on a connection that failed to open
		private RealtimeConnection Connection
		{
			get
			{
				if (Context.Items.TryGetValue(ConnectionItem, out object? item) && item is RealtimeConnection connection
					&& !connection.OwnerSession.IsRetired
					&& ReferenceEquals(WebSocketManager.GetWebSocketForSession(connection.OwnerSession), connection))
				{
					return connection;
				}

				// a connection that lost its session is being closed, its calls are refused
				Context.Abort();
				throw new HubException("connection has no session");
			}
		}

		private UserSession Session => Connection.OwnerSession;
		private Int64 UserID => Connection.m_UserID;
		private EUserSessionType SessionType => Connection.m_SessionType;

		// The session type matrix of section 9, the param names the missing capability
		private Failure? RequireAccess(ESessionAccessType access)
		{
			if (SessionHelpers.SessionTypeHasAccessTo(SessionType, access))
			{
				return null;
			}

			string capability = access switch
			{
				ESessionAccessType.ServerListReadOnly => "session.lobby_list",
				ESessionAccessType.Gameplay => "session.gameplay",
				_ => "session." + access.ToString().ToLowerInvariant()
			};
			return Result.Forbidden(capability);
		}

		// Checks one of the perm:<permission> policies against the caller
		private async Task<Failure?> RequirePermissionAsync(string permission)
		{
			AuthorizationResult result = await m_authorization.AuthorizeAsync(Context.User!, PermissionPolicies.For(permission));
			return result.Succeeded ? null : Result.Forbidden(permission);
		}

		// Implemented by the area files

		private partial Task SendFriendsSnapshotAsync(UserSession session);
		private partial Task RestoreRoomAsync(UserSession session);
		private partial Task RestoreLobbyAsync(UserSession session);
		private partial Task OnConnectionOpenedAsync(UserSession session);
		private partial Task OnConnectionClosedAsync(UserSession session);
	}

	// Reads the session principal built by SessionTokenHandler
	public static class RealtimeClaims
	{
		public static Int64 UserID(ClaimsPrincipal user)
		{
			return Int64.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out Int64 id) ? id : -1;
		}

		public static EUserSessionType SessionType(ClaimsPrincipal user)
		{
			return int.TryParse(user.FindFirst("session_type")?.Value, out int value) && Enum.IsDefined(typeof(EUserSessionType), value)
				? (EUserSessionType)value
				: EUserSessionType.None;
		}

		public static KnownClients.EKnownClients ClientID(ClaimsPrincipal user)
		{
			string? clientID = user.FindFirst(SessionTokenHandler.ClientIDClaim)?.Value;
			return SessionRevocation.TryParseKnownClient(clientID, out KnownClients.EKnownClients known) ? known : KnownClients.EKnownClients.unknown;
		}

		public static string? AuthorizationID(ClaimsPrincipal user) => user.FindFirst(SessionTokenHandler.AuthorizationIDClaim)?.Value;
	}
}
