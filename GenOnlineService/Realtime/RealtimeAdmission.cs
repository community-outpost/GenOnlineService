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
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using System.Security.Claims;

namespace GenOnlineService.Realtime
{
	// Who may open a realtime connection. Behind the Cloudflare tunnel an HTTP status on the upgrade reaches the client only as
	// a bare 502, so the service accepts every upgrade and refuses after the handshake, before Ready, with a close code and,
	// where the client needs params, the event that carries them (docs/realtime-protocol.md section 3)
	public static class RealtimeAdmission
	{
		// The session types the hub serves, the roles of its [Authorize]
		public const string Roles = "GameClient,ChatClient,GameLauncher";

		public const int StandbyRetryAfterMs = 1000;

		public sealed record Refusal(string Code, bool AllowReconnect, InvocationMessage? Before);

		// A WebSocket upgrade to the hub, the only request these refusals replace a status for
		public static bool IsRealtimeUpgrade(HttpContext context) => context.WebSockets.IsWebSocketRequest && context.Request.Path.StartsWithSegments(RealtimeHub.Path);

		// A copy that is not serving sends the caller to the one that is
		public static Refusal Standby() => new(CloseCodes.ServiceUnavailable, true,
			new InvocationMessage(nameof(IRealtimeClient.ServerSwitching), new object?[] { new ServerSwitchingEvent { RetryAfterMs = StandbyRetryAfterMs } }));

		// Null when the caller is served. Standby first, so a standby reads nothing about the caller; the registration is read last, only for a game client
		public static async Task<Refusal?> CheckAsync(bool serving, ClaimsPrincipal user, PlayBan? ban, Func<Task<GameSessionRegistration?>> loadRegistration)
		{
			if (!serving)
			{
				return Standby();
			}

			if (ban != null)
			{
				ModerationNoticeEvent notice = new() { Action = ModerationAction.Ban, Reason = String.IsNullOrEmpty(ban.Reason) ? null : ban.Reason, ExpiresAt = ban.ExpiresAt };
				return new Refusal(CloseCodes.ModerationBanned, false, new InvocationMessage(nameof(IRealtimeClient.ModerationNotice), new object?[] { notice }));
			}

			EUserSessionType sessionType = RealtimeClaims.SessionType(user);
			bool hasRole = Roles.Split(',').Any(user.IsInRole);
			if (user.Identity?.IsAuthenticated != true || !hasRole || RealtimeClaims.UserID(user) <= 0 || sessionType == EUserSessionType.None)
			{
				return new Refusal(CloseCodes.Unauthorized, false, null);
			}

			if (sessionType == EUserSessionType.GameClient)
			{
				GameSessionRegistration? registration = await loadRegistration();
				if (registration == null || registration.AuthorizationID != RealtimeClaims.AuthorizationID(user))
				{
					return new Refusal(CloseCodes.SessionNotRegistered, false, null);
				}
			}

			return null;
		}

		// Writes the event, then the close record, then drops the transport
		public static async Task CloseAsync(HubConnectionContext transport, Refusal refusal)
		{
			try
			{
				using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
				if (refusal.Before != null)
				{
					await transport.WriteAsync(refusal.Before, timeout.Token);
				}
				await transport.WriteAsync(new CloseMessage(refusal.Code, refusal.AllowReconnect), timeout.Token);
			}
			catch (Exception)
			{
			}
			transport.Abort();
		}
	}

	// Runs no hub method on a connection the hub did not admit: a refused caller is closed in OnConnectedAsync, this keeps a
	// message that arrives meanwhile from running even where a method does not check the caller itself
	public sealed class RealtimeAdmittedFilter : IHubFilter
	{
		public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
		{
			if (!invocationContext.Context.Items.ContainsKey(RealtimeHub.ConnectionItem))
			{
				invocationContext.Context.Abort();
				return null;
			}

			return await next(invocationContext);
		}
	}
}
