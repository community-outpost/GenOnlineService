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

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GenOnlineService.Identity
{
	public sealed record PlayBan(string Reason, DateTime? Until);

	// User state and play bans for every request, reloaded on the reconcile tick; no database read per request
	public static class AccessControl
	{
		private static readonly ILogger s_log = AppLog.For(typeof(AccessControl));

		private static volatile Dictionary<Int64, string> s_inactive = new();
		private static volatile Dictionary<Int64, PlayBan> s_banned = new();
		private static bool s_seeded;

		public static bool IsInactive(Int64 userID) => s_inactive.ContainsKey(userID);

		public static PlayBan? GetPlayBan(Int64 userID) => s_banned.GetValueOrDefault(userID);

		private static readonly SemaphoreSlim s_reloadGate = new SemaphoreSlim(1, 1);

		// Moderation calls this right after a ban as well as the reconcile tick, so reloads never overlap
		public static async Task Reload(AppDbContext db)
		{
			await s_reloadGate.WaitAsync();
			try
			{
				await ReloadCore(db);
			}
			finally
			{
				s_reloadGate.Release();
			}
		}

		private static async Task ReloadCore(AppDbContext db)
		{
			DateTime now = DateTime.UtcNow;

			Dictionary<Int64, string> inactive = await db.Users.AsNoTracking()
				.Where(u => u.State != UserStates.Active)
				.ToDictionaryAsync(u => u.ID, u => u.State);

			List<Ban> bans = await db.Bans.AsNoTracking()
				.Where(b => b.Restriction == BanRestrictions.Play && b.RevokedAt == null && (b.ExpiresAt == null || b.ExpiresAt > now))
				.ToListAsync();

			// the longest lasting ban decides what the player sees
			Dictionary<Int64, PlayBan> banned = bans
				.GroupBy(b => b.UserID)
				.ToDictionary(g => g.Key, g =>
				{
					Ban longest = g.OrderByDescending(b => b.ExpiresAt == null).ThenByDescending(b => b.ExpiresAt).First();
					return new PlayBan(longest.Reason, longest.ExpiresAt);
				});

			Dictionary<Int64, string> previousInactive = s_inactive;
			Dictionary<Int64, PlayBan> previousBanned = s_banned;
			bool bSeeded = s_seeded;

			s_inactive = inactive;
			s_banned = banned;
			s_seeded = true;

			// the first load only seeds, so a restart does not act on every existing ban again
			if (!bSeeded)
			{
				return;
			}

			foreach (Int64 userID in inactive.Keys.Where(id => !previousInactive.ContainsKey(id)))
			{
				s_log.LogInformation("User {UserId} became {State}: revoking every authorization", userID, inactive[userID]);
				await db.Users.Where(u => u.ID == userID).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")));
				await SessionRevocation.RevokeUser(userID, false, "account " + inactive[userID]);
			}

			foreach ((Int64 userID, PlayBan ban) in banned.Where(b => !previousBanned.ContainsKey(b.Key)))
			{
				s_log.LogInformation("User {UserId} was banned: revoking game client authorizations", userID);
				await SessionRevocation.RevokeUser(userID, true, "banned", ban);
			}
		}
	}

	// The build identity is read by realtime instead of being sent again with every lobby and queue call
	// OpenIddict keeps a row per issued token; expired and revoked rows are removed once a day
	public static class OAuthMaintenance
	{
		private static readonly ILogger s_log = AppLog.For(typeof(OAuthMaintenance));
		private static DateTime s_lastPrune = DateTime.MinValue;

		public static async Task PruneDaily(IServiceProvider services)
		{
			if (DateTime.UtcNow - s_lastPrune < TimeSpan.FromDays(1))
			{
				return;
			}

			s_lastPrune = DateTime.UtcNow;
			using IServiceScope scope = services.CreateScope();
			DateTimeOffset threshold = DateTimeOffset.UtcNow.AddDays(-1);
			long tokens = await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(threshold);
			long authorizations = await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(threshold);
			s_log.LogInformation("Pruned {Tokens} OAuth tokens and {Authorizations} authorizations", tokens, authorizations);
		}
	}

	public sealed record GameSessionRegistration(string AuthorizationID, string ClientID, DateTime RegisteredAt, uint? ExeCrc, uint? IniCrc, int AnticheatID);

	// Game sessions registered through POST /api/v1/auth/session; in memory, so a restart asks clients to register again
	public static class GameSessions
	{
		private static readonly ConcurrentDictionary<(Int64, EUserSessionType), GameSessionRegistration> s_sessions = new();

		public static void Register(Int64 userID, EUserSessionType sessionType, GameSessionRegistration registration)
		{
			s_sessions[(userID, sessionType)] = registration;
		}

		public static GameSessionRegistration? Get(Int64 userID, EUserSessionType sessionType)
		{
			return s_sessions.GetValueOrDefault((userID, sessionType));
		}

		public static bool Remove(Int64 userID, EUserSessionType sessionType)
		{
			return s_sessions.TryRemove((userID, sessionType), out _);
		}

		public static void RemoveUser(Int64 userID, bool bOnlyForAuthorizations, IReadOnlySet<string>? authorizationIDs = null)
		{
			foreach (var key in s_sessions.Keys.Where(k => k.Item1 == userID))
			{
				if (!bOnlyForAuthorizations || (authorizationIDs != null && s_sessions.TryGetValue(key, out GameSessionRegistration? r) && authorizationIDs.Contains(r.AuthorizationID)))
				{
					s_sessions.TryRemove(key, out _);
				}
			}
		}
	}

	public static class SessionRevocation
	{
		private static readonly ILogger s_log = AppLog.For(typeof(SessionRevocation));

		public const string AuthRevokedCode = "auth.revoked";

		// A client that registers a game, chat or launcher session is a game client; anything else (the website) is not
		public static bool IsGameClient(string? clientID)
		{
			return TryParseKnownClient(clientID, out KnownClients.EKnownClients known) && KnownClients.KnownClientSessionTypes.ContainsKey(known);
		}

		// Client ids are names; a numeric string never maps onto an enum value
		public static bool TryParseKnownClient(string? clientID, out KnownClients.EKnownClients known)
		{
			known = KnownClients.EKnownClients.unknown;
			return !String.IsNullOrEmpty(clientID)
				&& !Char.IsDigit(clientID[0]) && clientID[0] != '-'
				&& Enum.TryParse(clientID, false, out known)
				&& known != KnownClients.EKnownClients.unknown;
		}

		// Revokes the user's authorizations and their tokens, drops registered sessions and closes realtime
		public static async Task<int> RevokeUser(Int64 userID, bool bOnlyGameClients, string reason, PlayBan? ban = null)
		{
			var revoked = new HashSet<string>(StringComparer.Ordinal);

			try
			{
				using IServiceScope scope = ServiceLocator.Services.CreateScope();
				var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
				var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
				var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

				var targets = new List<object>();
				await foreach (object authorization in authorizations.FindBySubjectAsync(userID.ToString()))
				{
					if (!await authorizations.HasStatusAsync(authorization, OpenIddictConstants.Statuses.Valid))
					{
						continue;
					}

					if (bOnlyGameClients)
					{
						string? applicationID = await authorizations.GetApplicationIdAsync(authorization);
						object? application = applicationID == null ? null : await applications.FindByIdAsync(applicationID);
						if (!IsGameClient(application == null ? null : await applications.GetClientIdAsync(application)))
						{
							continue;
						}
					}

					targets.Add(authorization);
				}

				foreach (object authorization in targets)
				{
					string? id = await authorizations.GetIdAsync(authorization);
					await authorizations.TryRevokeAsync(authorization);
					if (id != null)
					{
						await tokens.RevokeByAuthorizationIdAsync(id);
						revoked.Add(id);
					}
				}
			}
			catch (Exception ex)
			{
				// the in-memory checks still refuse the user; a failed revocation is only logged
				s_log.LogError(ex, "Revoking authorizations of user {UserId} failed", userID);
			}

			GameSessions.RemoveUser(userID, bOnlyGameClients, revoked);

			if (ban != null)
			{
				await ModerationManager.DisconnectUser(userID, EModerationAction.Ban, ban.Reason, ban.Until);
			}
			else
			{
				await CloseRealtime(userID, null);
			}

			s_log.LogInformation("Revoked {Count} authorization(s) of user {UserId}: {Reason}", revoked.Count, userID, reason);
			return revoked.Count;
		}

		// Closes the user's realtime connections with auth.revoked, all or one session type
		public static Task CloseRealtime(Int64 userID, EUserSessionType? onlySessionType)
		{
			return WebSocketManager.DisconnectUser(userID, AuthRevokedCode, allowReconnect: false, notice: null, onlySessionType);
		}
	}

	// Authenticates the OpenIddict access tokens of this environment and turns them into the session principal controllers use
	public sealed class SessionTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
	{
		public const string SchemeName = "session";
		public const string AuthenticationType = "session";
		public const string SessionTypeClaim = "session_type";
		public const string ClientIDClaim = "client_id";
		public const string AuthorizationIDClaim = "oi_au_id";

		private const string BanItemKey = "go_play_ban";
		private static readonly ILogger s_log = AppLog.For(typeof(SessionTokenHandler));

		public SessionTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
			: base(options, logger, encoder)
		{
		}

		protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			if (!Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
			{
				return AuthenticateResult.NoResult();
			}

			AuthenticateResult validated = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
			if (!validated.Succeeded || validated.Principal == null)
			{
				return AuthenticateResult.Fail("invalid token");
			}

			ClaimsPrincipal token = validated.Principal;
			if (!Int64.TryParse(token.GetClaim(OpenIddictConstants.Claims.Subject), out Int64 userID) || userID <= 0)
			{
				return AuthenticateResult.Fail("invalid subject");
			}

			if (AccessControl.IsInactive(userID))
			{
				s_log.LogInformation("Refused access token of inactive user {UserId}", userID);
				return AuthenticateResult.Fail("inactive user");
			}

			PlayBan? ban = AccessControl.GetPlayBan(userID);
			if (ban != null)
			{
				Context.Items[BanItemKey] = ban;
				return AuthenticateResult.Fail("banned");
			}

			string clientID = token.GetClaim(OpenIddictConstants.Claims.ClientId) ?? token.GetClaim(OpenIddictConstants.Claims.AuthorizedParty) ?? String.Empty;
			var claims = new List<Claim>
			{
				new Claim(ClaimTypes.NameIdentifier, userID.ToString()),
				new Claim(JwtRegisteredClaimNames.Name, token.GetClaim(OpenIddictConstants.Claims.Name) ?? String.Empty),
				new Claim(JwtRegisteredClaimNames.Address, IPHelpers.NormalizeIP(Context.Connection.RemoteIpAddress?.ToString())),
				new Claim(ClientIDClaim, clientID),
				new Claim(AuthorizationIDClaim, token.GetClaim(OpenIddictConstants.Claims.Private.AuthorizationId) ?? String.Empty)
			};

			// game roles come from a first-party client and the game scope, never from the token itself
			if (token.HasScope(AccountsSettings.GameScope)
				&& SessionRevocation.TryParseKnownClient(clientID, out KnownClients.EKnownClients known)
				&& KnownClients.KnownClientSessionTypes.TryGetValue(known, out EUserSessionType sessionType))
			{
				claims.Add(new Claim(SessionTypeClaim, ((int)sessionType).ToString()));
				claims.Add(new Claim(ClaimTypes.Role, "Player"));
				claims.Add(new Claim(ClaimTypes.Role, sessionType.ToString()));
			}

			var identity = new ClaimsIdentity(claims, AuthenticationType, JwtRegisteredClaimNames.Name, ClaimTypes.Role);
			return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
		}

		protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
		{
			if (Context.Items.TryGetValue(BanItemKey, out object? value) && value is PlayBan ban && !Response.HasStarted)
			{
				Response.StatusCode = StatusCodes.Status403Forbidden;
				string? until = ban.Until?.ToString("O");

				if (Context.WebSockets.IsWebSocketRequest)
				{
					await Response.WriteAsJsonAsync(new { code = "moderation.banned", @params = new { reason = ban.Reason, until } });
				}
				else
				{
					await Response.WriteAsJsonAsync(new { message = "403 Forbidden - banned", error = "moderation.banned", reason = ban.Reason, until });
				}

				return;
			}

			await UniformAuthResponses.WriteUnauthorized(Context);
		}
	}
}
