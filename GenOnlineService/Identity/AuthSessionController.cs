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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenOnlineService.Identity
{
	public sealed class AuthSessionRequest
	{
		[JsonPropertyName("machine_guid")] public string? MachineGuid { get; set; }
		[JsonPropertyName("mac_addr")] public string? MacAddress { get; set; }
		[JsonPropertyName("vol_serial")] public string? VolumeSerial { get; set; }

		// Build identity, sent once per session; required for game sessions
		[JsonPropertyName("exe_crc")] public uint? ExeCrc { get; set; }
		[JsonPropertyName("ini_crc")] public uint? IniCrc { get; set; }
		[JsonPropertyName("anticheat_id")] public int? AnticheatID { get; set; }
	}

	// Registers the game session of an access token; it mints no tokens, the OAuth endpoints do that
	[ApiController]
	public sealed class AuthSessionController : ControllerBase
	{
		private const int MaximumDeviceValueLength = 256;

		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly IOpenIddictAuthorizationManager m_authorizations;
		private readonly IOpenIddictTokenManager m_tokens;

		public AuthSessionController(IDbContextFactory<AppDbContext> dbFactory, IOpenIddictAuthorizationManager authorizations, IOpenIddictTokenManager tokens)
		{
			m_dbFactory = dbFactory;
			m_authorizations = authorizations;
			m_tokens = tokens;
		}

		private static object Problem(string error, string message) => new { message, error };

		[HttpPost("~/api/v1/auth/session")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task<IActionResult> Register([FromBody] AuthSessionRequest? body)
		{
			Int64 userID = TokenHelper.GetUserID(this);
			EUserSessionType sessionType = TokenHelper.GetSessionType(this);
			KnownClients.EKnownClients clientID = TokenHelper.GetClientID(this);
			string authorizationID = User.FindFirst(SessionTokenHandler.AuthorizationIDClaim)?.Value ?? String.Empty;

			if (userID <= 0 || sessionType == EUserSessionType.None || clientID == KnownClients.EKnownClients.unknown || authorizationID.Length == 0)
			{
				return StatusCode(StatusCodes.Status403Forbidden, Problem("session.forbidden", "This token cannot register a game session."));
			}

			// a signed-out or revoked sign-in cannot register again while its access token is still unexpired
			object? authorization = await m_authorizations.FindByIdAsync(authorizationID);
			if (authorization == null || !await m_authorizations.HasStatusAsync(authorization, OpenIddictConstants.Statuses.Valid)
				|| await m_authorizations.GetSubjectAsync(authorization) != userID.ToString())
			{
				return StatusCode(StatusCodes.Status401Unauthorized, new { message = "401 Unauthorized", error = "unauthorized" });
			}

			body ??= new AuthSessionRequest();
			if (new[] { body.MachineGuid, body.MacAddress, body.VolumeSerial }.Any(v => v != null && v.Length > MaximumDeviceValueLength))
			{
				return BadRequest(Problem("invalid_argument", "A device value is too long."));
			}

			if (sessionType == EUserSessionType.GameClient && (body.ExeCrc == null || body.IniCrc == null || body.AnticheatID == null))
			{
				return BadRequest(Problem("invalid_argument", "A game session needs exe_crc, ini_crc and anticheat_id."));
			}

			string ipAddress = IPHelpers.NormalizeIP(HttpContext.Connection.RemoteIpAddress?.ToString());
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();

			if (sessionType == EUserSessionType.GameClient)
			{
				await Database.UserDevices.RegisterUserDevice(db, userID, body.MachineGuid ?? "NONE", body.MacAddress ?? "NONE", body.VolumeSerial ?? "NONE", ipAddress);
			}

			string displayName = await Database.Users.GetDisplayName(db, userID);

			// a new session replaces the previous one of this type: its lobby, matchmaking and socket state go
			await SessionHelpers.SetUsedLoggedIn(userID, clientID, sessionType);
			await WebSocketManager.ClearDataFromUser(userID, sessionType);
			GameSessions.Register(userID, sessionType, new GameSessionRegistration(authorizationID, clientID.ToString(), DateTime.UtcNow, body.ExeCrc, body.IniCrc, body.AnticheatID ?? -1));
			AppMetrics.RecordAuthentication("session", clientID.ToString(), "success");

			return Ok(new
			{
				user_id = userID,
				display_name = displayName,
				session_type = sessionType.ToString(),
				ws_url = $"{(Request.IsHttps ? "wss" : "ws")}://{Request.Host}{Request.PathBase}{GenOnlineService.Realtime.RealtimeHub.Path}"
			});
		}

		// Logout: drops the game session and its realtime connection, and revokes this sign-in's refresh chain
		[HttpDelete("~/api/v1/auth/session")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task<IActionResult> Unregister()
		{
			Int64 userID = TokenHelper.GetUserID(this);
			EUserSessionType sessionType = TokenHelper.GetSessionType(this);
			string? authorizationID = User.FindFirst(SessionTokenHandler.AuthorizationIDClaim)?.Value;

			// only this sign-in ends, another install of the same session type keeps its session
			if (String.IsNullOrEmpty(authorizationID))
			{
				GameSessions.Remove(userID, sessionType);
				await SessionRevocation.CloseRealtime(userID, sessionType);
				await WebSocketManager.ClearDataFromUser(userID, sessionType);
				return NoContent();
			}

			// the registration goes first and the revocation before the close, so a connection still opening is refused when it checks again
			GameSessions.RemoveSignIn(authorizationID);
			try
			{
				object? authorization = await m_authorizations.FindByIdAsync(authorizationID);
				if (authorization != null && await m_authorizations.GetSubjectAsync(authorization) == userID.ToString())
				{
					await m_authorizations.TryRevokeAsync(authorization);
					await m_tokens.RevokeByAuthorizationIdAsync(authorizationID);
				}
			}
			finally
			{
				// also closes the realtime of this sign-in when the revocation fails
				await SignInRevocation.EndAsync(authorizationID);
			}

			return NoContent();
		}

		// Sign out everywhere for one user (website account security page); privileged, so only through a CLI-minted token
		[HttpPost("~/api/v1/users/{id:long}/revoke_sessions")]
		[Authorize(Policy = PermissionPolicies.Prefix + Permissions.UsersRevoke)]
		public async Task<IActionResult> RevokeSessions(Int64 id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await db.Users.AnyAsync(u => u.ID == id))
			{
				return NotFound(Problem("user.not_found", "404 User Not Found"));
			}

			int revoked = await SessionRevocation.RevokeUser(id, false, "revoke_sessions");

			db.AuditLog.Add(new AuditLogEntry
			{
				CreatedAt = DateTime.UtcNow,
				ActorKind = AuditActorKinds.ServiceAccount,
				ActorID = ApiTokenPrincipals.TokenID(User),
				Actor = User.Identity?.Name ?? "token",
				Action = "revoke_sessions",
				TargetKind = AuditTargetKinds.User,
				TargetID = id,
				Source = "api",
				Details = JsonSerializer.Serialize(new { authorizations = revoked })
			});
			await db.SaveChangesAsync();

			return Ok(new { revoked_authorizations = revoked });
		}
	}
}
