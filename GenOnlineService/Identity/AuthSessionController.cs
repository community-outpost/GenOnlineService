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

		// Every REST failure is {"error": "<code>", "params": {...}}
		private static object Problem(string error, object? parameters = null) => new { error, @params = parameters ?? new { } };

		[EndpointSummary("Registers the game session of the access token.")]
		[ProducesResponseType<GenOnlineService.OpenApi.AuthSessionResponse>(StatusCodes.Status200OK)]
		[HttpPost("~/api/v1/auth/session")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task<IActionResult> Register([FromBody] AuthSessionRequest? body)
		{
			Int64 userID = TokenHelper.GetUserID(this);
			EUserSessionType sessionType = TokenHelper.GetSessionType(this);
			RegisteredClient? client = ClientRegistry.FindEnabled(TokenHelper.GetClientID(this));
			string authorizationID = User.FindFirst(SessionTokenHandler.AuthorizationIDClaim)?.Value ?? String.Empty;

			if (userID <= 0 || sessionType == EUserSessionType.None || client == null || client.SessionType != sessionType || authorizationID.Length == 0)
			{
				return StatusCode(StatusCodes.Status403Forbidden, Problem("session.forbidden"));
			}

			// a signed-out or revoked sign-in cannot register again while its access token is still unexpired
			object? authorization = await m_authorizations.FindByIdAsync(authorizationID);
			if (authorization == null || !await m_authorizations.HasStatusAsync(authorization, OpenIddictConstants.Statuses.Valid)
				|| await m_authorizations.GetSubjectAsync(authorization) != userID.ToString())
			{
				return StatusCode(StatusCodes.Status401Unauthorized, UniformAuthResponses.UnauthorizedBody);
			}

			body ??= new AuthSessionRequest();
			string? tooLong = new[] { ("machine_guid", body.MachineGuid), ("mac_addr", body.MacAddress), ("vol_serial", body.VolumeSerial) }
				.FirstOrDefault(v => v.Item2 != null && v.Item2.Length > MaximumDeviceValueLength).Item1;
			if (tooLong != null)
			{
				return BadRequest(Problem("invalid_argument", new { field = tooLong }));
			}

			// a game session needs its build identity: exe_crc, ini_crc and anticheat_id
			string? missing = sessionType != EUserSessionType.GameClient ? null
				: body.ExeCrc == null ? "exe_crc" : body.IniCrc == null ? "ini_crc" : body.AnticheatID == null ? "anticheat_id" : null;
			if (missing != null)
			{
				return BadRequest(Problem("invalid_argument", new { field = missing }));
			}

			// a client that lists its builds only plays with one of them; an unlisted build is left to anti-cheat
			if (sessionType == EUserSessionType.GameClient && !client.AcceptsBuild(body.ExeCrc!.Value, body.IniCrc!.Value))
			{
				AppMetrics.RecordAuthentication("session", client.ClientID, "build_not_accepted");
				return StatusCode(StatusCodes.Status403Forbidden, Problem("session.build_not_accepted"));
			}

			string ipAddress = IPHelpers.NormalizeIP(HttpContext.Connection.RemoteIpAddress?.ToString());
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();

			if (sessionType == EUserSessionType.GameClient)
			{
				await Database.UserDevices.RegisterUserDevice(db, userID, body.MachineGuid ?? "NONE", body.MacAddress ?? "NONE", body.VolumeSerial ?? "NONE", ipAddress);
			}

			(string Name, int Tag) name = await Database.DisplayNames.Get(db, userID) ?? (String.Empty, 0);

			// a new session replaces the previous one of this type: its lobby, matchmaking and socket state go
			await SessionHelpers.SetUsedLoggedIn(userID, client.ClientID, sessionType);
			await WebSocketManager.ClearDataFromUser(userID, sessionType);
			await GameSessions.RegisterAsync(db, userID, sessionType, new GameSessionRegistration(authorizationID, client.ClientID, client.Game, DateTime.UtcNow, body.ExeCrc, body.IniCrc, body.AnticheatID ?? -1));
			AppMetrics.RecordAuthentication("session", client.ClientID, "success");
			GenOnlineService.Rules.RuleRunner.Raise(GenOnlineService.Rules.RuleEvent.Of(GenOnlineService.Rules.RuleTriggers.GameSession, userID, ipAddress, (GenOnlineService.Rules.RuleEventFields.Client, client.ClientID)));

			return Ok(new
			{
				user_id = userID,
				display_name = name.Name,
				name_tag = Database.DisplayNames.FormatTag(name.Tag),
				session_type = sessionType.ToString(),
				ws_url = $"{(Request.IsHttps ? "wss" : "ws")}://{Request.Host}{Request.PathBase}{GenOnlineService.Realtime.RealtimeHub.Path}"
			});
		}

		// Logout: drops the game session and its realtime connection, and revokes this sign-in's refresh chain
		[EndpointSummary("Ends the game session of this sign-in and revokes its refresh tokens.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
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
				await GameSessions.RemoveAsync(userID, sessionType);
				await SessionRevocation.CloseRealtime(userID, sessionType);
				await WebSocketManager.ClearDataFromUser(userID, sessionType);
				return NoContent();
			}

			// the registration goes first and the revocation before the close, so a connection still opening is refused when it checks again
			await GameSessions.RemoveSignInAsync(authorizationID);
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
		[EndpointSummary("Signs a user out everywhere.")]
		[ProducesResponseType<GenOnlineService.OpenApi.RevokeSessionsResponse>(StatusCodes.Status200OK)]
		[HttpPost("~/api/v1/users/{id:long}/revoke_sessions")]
		[Authorize(Policy = PermissionPolicies.Prefix + Permissions.UsersRevoke)]
		public async Task<IActionResult> RevokeSessions(Int64 id)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			if (!await db.Users.AnyAsync(u => u.ID == id))
			{
				return NotFound(Problem("not_found", new { what = "user" }));
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
