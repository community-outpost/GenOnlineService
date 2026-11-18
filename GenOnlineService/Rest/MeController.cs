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
using GenOnlineService.OpenApi;
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
		[EndpointSummary("Reads the account of the caller.")]
		[ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/me")]
		public async Task<IActionResult> GetMe()
		{
			(string Name, int Tag)? name;
			await using (AppDbContext db = await m_dbFactory.CreateDbContextAsync())
			{
				name = await Database.DisplayNames.Get(db, Me);
			}

			return RestResults.Ok(new MeResponse
			{
				UserId = Me,
				DisplayName = name?.Name ?? String.Empty,
				Tag = PlayerTags.Of(name?.Tag ?? 0),
				Roles = StaffRoles.RolesOf(Me).Order(StringComparer.Ordinal).ToList(),
				Permissions = StaffRoles.PermissionsOf(Me).Order(StringComparer.Ordinal).ToList()
			});
		}

		[EndpointSummary("Lists the game sessions of the caller.")]
		[ProducesResponseType<MeSessionsResponse>(StatusCodes.Status200OK)]
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

	// The accounts a game session links at a platform (section 10.4); the provider decides what the token and the proof are
	public sealed class MeController : ApiControllerBase
	{
		private readonly IEnumerable<ILinkedAccountProvider> m_providers;

		public MeController(IEnumerable<ILinkedAccountProvider> providers, IAuthorizationService authorization) : base(authorization)
		{
			m_providers = providers;
		}

		// provider names are opaque and case-sensitive
		private ILinkedAccountProvider? Find(string provider) => m_providers.FirstOrDefault(p => p.Name == provider);

		[EndpointSummary("Issues a short-lived token to sign in to a linked account provider.")]
		[ProducesResponseType<SignInTokenResponse>(StatusCodes.Status200OK)]
		[HttpPost("~/api/v1/me/linked_accounts/{provider}/sign_in_token")]
		public IActionResult IssueSignInToken(string provider)
		{
			ILinkedAccountProvider? linked = Find(provider);
			if (linked == null)
			{
				return RestResults.NotFound("provider");
			}

			if (!IsGameSession())
			{
				return RestResults.Fail(RestCodes.SessionGameRequired);
			}

			Result<SignInToken> issued = linked.IssueSignInToken(Me, TokenHelper.GetDisplayName(this));
			if (!issued.Ok)
			{
				return RestResults.Fail(new Failure(issued.Code!, issued.Params!));
			}
			return RestResults.Ok(new SignInTokenResponse { Token = issued.Data!.Token, ExpiresIn = issued.Data.ExpiresIn });
		}

		[EndpointSummary("Links an account of another provider to the caller.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[ApiRequestBody(typeof(LinkAccountRequest))]
		[HttpPut("~/api/v1/me/linked_accounts/{provider}")]
		public async Task<IActionResult> LinkAccount(string provider)
		{
			ILinkedAccountProvider? linked = Find(provider);
			if (linked == null)
			{
				return RestResults.NotFound("provider");
			}

			(LinkAccountRequest? body, Failure? malformed) = await ReadBodyAsync<LinkAccountRequest>();
			if ((malformed ?? LinkedAccounts.ValidateRequest(body)) is Failure invalid)
			{
				return RestResults.Fail(invalid);
			}

			if (!IsGameSession())
			{
				return RestResults.Fail(RestCodes.SessionGameRequired);
			}

			Failure? failure = await linked.LinkAsync(Me, RealtimeClaims.SessionType(User), body!.Proof!);
			return failure is Failure failed ? RestResults.Fail(failed) : NoContent();
		}

		private bool IsGameSession() => Me != -1 && SessionHelpers.SessionTypeHasAccessTo(RealtimeClaims.SessionType(User), ESessionAccessType.Gameplay);
	}
}
