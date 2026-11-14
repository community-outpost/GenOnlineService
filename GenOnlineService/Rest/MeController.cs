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

using GenOnlineService.Api;
using GenOnlineService.Identity;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rest
{
	// The caller's profile and registered sessions, for every session client: game, launcher, chat and the website
	[ApiController]
	[Authorize(AuthenticationSchemes = SessionTokenHandler.SchemeName)]
	public sealed class MeProfileController : ControllerBase
	{
		private static readonly (EUserSessionType SessionType, SessionKind Kind)[] s_sessionKinds =
		{
			(EUserSessionType.GameClient, SessionKind.Game),
			(EUserSessionType.GameLauncher, SessionKind.Launcher),
			(EUserSessionType.ChatClient, SessionKind.Chat)
		};

		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public MeProfileController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		private Int64 Me => RealtimeClaims.UserID(User);

		// Roles and permissions come from the roles the service holds, never from the token
		[HttpGet("~/api/v1/me")]
		public async Task<IActionResult> GetMe()
		{
			string displayName;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				displayName = await Database.Users.GetDisplayName(db, Me);
			}

			return RestResults.Ok(new MeResponse
			{
				UserId = Me,
				DisplayName = displayName,
				Roles = StaffRoles.RolesOf(Me).Order(StringComparer.Ordinal).ToList(),
				Permissions = StaffRoles.PermissionsOf(Me).Order(StringComparer.Ordinal).ToList()
			});
		}

		[HttpGet("~/api/v1/me/sessions")]
		public IActionResult GetSessions()
		{
			List<MeSession> sessions = new List<MeSession>();
			foreach ((EUserSessionType sessionType, SessionKind kind) in s_sessionKinds)
			{
				GameSessionRegistration? registration = GameSessions.Get(Me, sessionType);
				if (registration != null)
				{
					sessions.Add(new MeSession { Type = kind, ClientId = registration.ClientID, RegisteredAt = registration.RegisteredAt });
				}
			}
			return RestResults.Ok(new MeSessionsResponse { Sessions = sessions });
		}
	}

	// The EOS token and the linked EOS account, for game sessions
	public sealed class MeController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly LobbyManager m_lobbyManager;

		public MeController(IDbContextFactory<AppDbContext> dbFactory, LobbyManager lobbyManager, IAuthorizationService authorization) : base(authorization)
		{
			m_dbFactory = dbFactory;
			m_lobbyManager = lobbyManager;
		}

		[HttpPost("~/api/v1/me/eos_token")]
		public IActionResult IssueEosToken()
		{
			if (!IsGameSession())
			{
				return RestResults.Fail(RestCodes.SessionGameRequired);
			}

			EosTokenIssuer.Settings? settings = EosTokenIssuer.GetSettings();
			if (settings == null)
			{
				return RestResults.Fail(RestCodes.ServiceUnavailable, new { What = "eos_token" });
			}

			string token = EosTokenIssuer.Issue(settings, Me, TokenHelper.GetDisplayName(this), out _);
			return RestResults.Ok(new EosTokenResponse { EosToken = token, ExpiresIn = (int)settings.Lifetime.TotalSeconds });
		}

		// Only game clients link an EOS account
		[HttpPut("~/api/v1/me/linked_accounts/eos")]
		public async Task<IActionResult> LinkEos()
		{
			(LinkEosRequest? body, Failure? malformed) = await ReadBodyAsync<LinkEosRequest>();
			if ((malformed ?? EosLink.ValidateRequest(body)) is Failure invalid)
			{
				return RestResults.Fail(invalid);
			}

			if (!IsGameSession())
			{
				return RestResults.Fail(RestCodes.SessionGameRequired);
			}

			Failure? failure = await EosLink.LinkAsync(m_lobbyManager, Me, RealtimeClaims.SessionType(User), body!.IdToken!);
			return failure is Failure failed ? RestResults.Fail(failed) : NoContent();
		}

		private bool IsGameSession() => Me != -1 && SessionHelpers.SessionTypeHasAccessTo(RealtimeClaims.SessionType(User), ESessionAccessType.Gameplay);
	}
}
