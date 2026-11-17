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
	public sealed record PlayBan(string Reason, DateTime? ExpiresAt);

	// User state and play bans for every request, reloaded on the reconcile tick; no database read per request
	public static class AccessControl
	{
		private static readonly ILogger s_log = AppLog.For(typeof(AccessControl));

		private static volatile Dictionary<Int64, string> s_inactive = new();
		private static volatile Dictionary<Int64, PlayBan> s_banned = new();
		private static bool s_seeded;

		public static bool IsInactive(Int64 userID) => s_inactive.ContainsKey(userID);

		public static PlayBan? GetPlayBan(Int64 userID) => s_banned.GetValueOrDefault(userID);

		// Players who must choose a name before they play: a provider sign-up, or a name staff reset (S17)
		private static volatile HashSet<Int64> s_nameRequired = new();

		public static bool MustChooseName(Int64 userID) => s_nameRequired.Contains(userID);

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

			List<Sanction> bans = await Database.Moderation.ActiveSanctions(db, SanctionTypes.Ban, now).ToListAsync();

			// the longest lasting ban decides what the player sees
			Dictionary<Int64, PlayBan> banned = bans
				.GroupBy(b => b.UserID)
				.ToDictionary(g => g.Key, g =>
				{
					Sanction longest = g.OrderByDescending(b => b.ExpiresAt == null).ThenByDescending(b => b.ExpiresAt).First();
					return new PlayBan(longest.Reason ?? String.Empty, longest.ExpiresAt);
				});

			HashSet<Int64> nameRequired = (await db.Users.AsNoTracking()
				.Where(u => u.NameRequiredAt != null && u.State == UserStates.Active)
				.Select(u => u.ID)
				.ToListAsync()).ToHashSet();

			Dictionary<Int64, string> previousInactive = s_inactive;
			Dictionary<Int64, PlayBan> previousBanned = s_banned;
			HashSet<Int64> previousNameRequired = s_nameRequired;
			bool bSeeded = s_seeded;

			s_inactive = inactive;
			s_banned = banned;
			s_nameRequired = nameRequired;
			s_seeded = true;

			// the first load only seeds, so a restart does not act on every existing ban again
			if (!bSeeded)
			{
				return;
			}

			foreach (Int64 userID in inactive.Keys.Where(id => !previousInactive.ContainsKey(id)))
			{
				s_log.LogInformation("User {UserId} became {State}: revoking every authorization", userID, inactive[userID]);
				await RevokeInactiveUser(db, userID, inactive[userID]);
			}

			foreach ((Int64 userID, PlayBan ban) in banned.Where(b => !previousBanned.ContainsKey(b.Key)))
			{
				s_log.LogInformation("User {UserId} was banned: revoking game client authorizations", userID);
				await SessionRevocation.RevokeUser(userID, true, "banned", ban);
			}

			// a name reset the API did not kick (the CLI runs apart from the service): a player still connected here is
			// signed out now, so the next sign-in asks for the new name (S17). New accounts are never connected yet.
			foreach (Int64 userID in nameRequired.Where(id => !previousNameRequired.Contains(id) && IsConnectedHere(id)))
			{
				s_log.LogInformation("User {UserId} must choose a new name: ending their sessions", userID);
				await SessionRevocation.RevokeUser(userID, false, "name reset");
			}
		}

		private static bool IsConnectedHere(Int64 userID)
		{
			return WebSocketManager.GetAllDataFromUser(userID).Count > 0
				|| Enum.GetValues<EUserSessionType>().Any(type => type != EUserSessionType.None && GameSessions.Get(userID, type) != null);
		}

		// The block path and the startup sweep share this, so the two never diverge
		public static async Task<int> RevokeInactiveUser(AppDbContext db, Int64 userID, string state)
		{
			await db.Users.Where(u => u.ID == userID).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")));
			return await SessionRevocation.RevokeUser(userID, false, "account " + state);
		}

		// A user blocked while the service was down kept their authorizations; startup finds them in one query and revokes them in the background
		public static async Task SweepBlockedUsers(IDbContextFactory<AppDbContext> dbFactory)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			string valid = OpenIddictConstants.Statuses.Valid;

			List<string> subjects = await db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking()
				.Where(a => a.Status == valid && db.Users.Any(u => u.State == UserStates.Blocked && u.ID.ToString() == a.Subject))
				.Select(a => a.Subject!)
				.Distinct()
				.ToListAsync();

			if (subjects.Count == 0)
			{
				return;
			}

			_ = Task.Run(async () =>
			{
				try
				{
					await using AppDbContext revokeDb = await dbFactory.CreateDbContextAsync();
					foreach (string subject in subjects)
					{
						if (!Int64.TryParse(subject, out Int64 userID))
						{
							continue;
						}

						int revoked = await RevokeInactiveUser(revokeDb, userID, UserStates.Blocked);
						if (revoked == 0)
						{
							continue;
						}

						revokeDb.AuditLog.Add(new AuditLogEntry
						{
							CreatedAt = DateTime.UtcNow,
							ActorKind = AuditActorKinds.Cli,
							Actor = "startup",
							Action = "revoke_sessions",
							TargetKind = AuditTargetKinds.User,
							TargetID = userID,
							Reason = "account blocked while the service was stopped",
							Source = "startup_sweep",
							Details = JsonSerializer.Serialize(new { authorizations = revoked })
						});
						await revokeDb.SaveChangesAsync();
					}

					s_log.LogInformation("Startup sweep revoked the sign-ins of {Count} blocked user(s)", subjects.Count);
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Startup sweep of blocked users failed");
				}
			});
		}
	}

	// The build identity is read by realtime instead of being sent again with every lobby and queue call
	// OpenIddict keeps a row per issued token; old dead rows are removed once a day
	// OpenIddict's own PruneAsync puts LIMIT inside an IN subquery, which MariaDB refuses, so the same rules run as plain batched deletes
	public static class OAuthMaintenance
	{
		private static readonly ILogger s_log = AppLog.For(typeof(OAuthMaintenance));
		private static DateTime s_lastPrune = DateTime.MinValue;

		private const int BatchSize = 1000;

		// redeemed refresh tokens stay this long so a late replay is still caught as reuse
		private static readonly TimeSpan s_retention = TimeSpan.FromDays(14);

		// an authorization without tokens is dead once its last refresh token could have expired
		private static readonly TimeSpan s_emptyAuthorizationAge = TimeSpan.FromDays(31);

		public static async Task PruneDaily(IServiceProvider services)
		{
			if (DateTime.UtcNow - s_lastPrune < TimeSpan.FromDays(1))
			{
				return;
			}

			s_lastPrune = DateTime.UtcNow;
			await using AppDbContext db = await services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
			(long tokens, long authorizations) = await Prune(db, DateTime.UtcNow);
			s_log.LogInformation("Pruned {Tokens} OAuth tokens and {Authorizations} authorizations", tokens, authorizations);
		}

		public static async Task<(long Tokens, long Authorizations)> Prune(AppDbContext db, DateTime now)
		{
			DateTime threshold = now - s_retention;
			DateTime emptyThreshold = now - s_emptyAuthorizationAge;
			string valid = OpenIddictConstants.Statuses.Valid;
			string inactive = OpenIddictConstants.Statuses.Inactive;

			IQueryable<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken> deadTokens = db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>()
				.Where(t => t.CreationDate < threshold
					&& ((t.Status != inactive && t.Status != valid)
						|| (t.Authorization != null && t.Authorization.Status != valid)
						|| t.ExpirationDate < now));

			IQueryable<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization> deadAuthorizations = db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>()
				.Where(a => !a.Tokens.Any()
					&& ((a.CreationDate < threshold && (a.Status != valid || a.Type == OpenIddictConstants.AuthorizationTypes.AdHoc))
						|| a.CreationDate < emptyThreshold));

			long tokens = await DeleteInBatches(deadTokens.Select(t => t.Id!), ids => db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>().Where(t => ids.Contains(t.Id!)).ExecuteDeleteAsync());
			long authorizations = await DeleteInBatches(deadAuthorizations.Select(a => a.Id!), ids => db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>().Where(a => ids.Contains(a.Id!)).ExecuteDeleteAsync());
			return (tokens, authorizations);
		}

		// ids are read first and deleted by value, so no provider sees LIMIT inside a subquery
		private static async Task<long> DeleteInBatches(IQueryable<string> ids, Func<List<string>, Task<int>> delete)
		{
			long total = 0;
			while (true)
			{
				List<string> batch = await ids.Take(BatchSize).ToListAsync();
				if (batch.Count == 0)
				{
					return total;
				}

				total += await delete(batch);
				if (batch.Count < BatchSize)
				{
					return total;
				}
			}
		}
	}

	// Game is the client's registered game (zerohour, generals or none), so build isolation keys on the game rather than the client id
	public sealed record GameSessionRegistration(string AuthorizationID, string ClientID, string Game, DateTime RegisteredAt, uint? ExeCrc, uint? IniCrc, int AnticheatID);

	// Game sessions registered through POST /api/v1/auth/session. The database holds them, so every copy and a restarted copy
	// accept the realtime upgrade of a registered client (docs/realtime-failover.md); memory is this copy's cache of them, which
	// the upgrade refreshes from the database, so the hub and lobby checks after it read it without a database call
	public static class GameSessions
	{
		private static readonly ILogger s_log = AppLog.For(typeof(GameSessions));
		private static readonly ConcurrentDictionary<(Int64, EUserSessionType), GameSessionRegistration> s_sessions = new();

		public static async Task RegisterAsync(AppDbContext db, Int64 userID, EUserSessionType sessionType, GameSessionRegistration registration)
		{
			await Database.GameSessionStore.Save(db, userID, sessionType, registration);
			s_sessions[(userID, sessionType)] = registration;
		}

		// This copy's cached registration, current for a client connected here
		public static GameSessionRegistration? Get(Int64 userID, EUserSessionType sessionType)
		{
			return s_sessions.GetValueOrDefault((userID, sessionType));
		}

		// The stored registration, also cached here; the realtime upgrade reads it so a registration made on another copy counts
		public static async Task<GameSessionRegistration?> LoadAsync(AppDbContext db, Int64 userID, EUserSessionType sessionType, CancellationToken cancellationToken = default)
		{
			GameSessionRegistration? registration = await Database.GameSessionStore.Load(db, userID, sessionType, cancellationToken);
			if (registration == null)
			{
				s_sessions.TryRemove((userID, sessionType), out _);
			}
			else
			{
				s_sessions[(userID, sessionType)] = registration;
			}
			return registration;
		}

		// Removals drop the cached entry first, so this copy refuses at once even when the database write fails
		public static Task RemoveAsync(Int64 userID, EUserSessionType sessionType)
		{
			s_sessions.TryRemove((userID, sessionType), out _);
			return Store(db => Database.GameSessionStore.Delete(db, userID, sessionType), "user " + userID);
		}

		public static Task RemoveSignInAsync(string authorizationID)
		{
			foreach (var entry in s_sessions.Where(e => e.Value.AuthorizationID == authorizationID))
			{
				s_sessions.TryRemove(entry);
			}
			return Store(db => Database.GameSessionStore.DeleteSignIn(db, authorizationID), "sign-in " + authorizationID);
		}

		public static Task RemoveUserAsync(Int64 userID, bool bOnlyForAuthorizations, IReadOnlySet<string>? authorizationIDs = null)
		{
			foreach (var key in s_sessions.Keys.Where(k => k.Item1 == userID))
			{
				if (!bOnlyForAuthorizations || (authorizationIDs != null && s_sessions.TryGetValue(key, out GameSessionRegistration? r) && authorizationIDs.Contains(r.AuthorizationID)))
				{
					s_sessions.TryRemove(key, out _);
				}
			}
			IReadOnlyCollection<string>? only = bOnlyForAuthorizations ? (IReadOnlyCollection<string>?)authorizationIDs ?? Array.Empty<string>() : null;
			return Store(db => Database.GameSessionStore.DeleteUser(db, userID, only), "user " + userID);
		}

		// Forgets this copy's cache, as a restarted copy or another copy starts; tests use it
		internal static void ForgetCached() => s_sessions.Clear();

		// A failed delete is logged: the cache already refuses here, and the revoked sign-in fails the upgrade's sign-in check on every copy
		private static async Task Store(Func<AppDbContext, Task> write, string what)
		{
			IDbContextFactory<AppDbContext>? factory = ServiceLocator.Services?.GetService<IDbContextFactory<AppDbContext>>();
			if (factory == null)
			{
				return;
			}
			try
			{
				await using AppDbContext db = await factory.CreateDbContextAsync();
				await write(db);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Removing the stored game session of {What} failed", what);
			}
		}
	}

	public static class SessionRevocation
	{
		private static readonly ILogger s_log = AppLog.For(typeof(SessionRevocation));

		public const string AuthRevokedCode = "auth.revoked";

		// A client whose kind opens a game, chat or launcher session is a game client; an app or the website is not
		public static bool IsGameClient(string? clientID)
		{
			RegisteredClient? client = ClientRegistry.Find(clientID);
			return client != null && client.SessionType != EUserSessionType.None;
		}

		// Revokes the user's authorizations and their tokens, drops registered sessions and closes realtime
		public static async Task<int> RevokeUser(Int64 userID, bool bOnlyGameClients, string reason, PlayBan? ban = null)
		{
			var revoked = new HashSet<string>(StringComparer.Ordinal);

			// a ban is announced before the revocation, which would otherwise close the game connections with auth.revoked first
			if (ban != null)
			{
				await ModerationManager.DisconnectUser(userID, EModerationAction.Ban, ban.Reason, ban.ExpiresAt);
			}

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

			await GameSessions.RemoveUserAsync(userID, bOnlyGameClients, revoked);

			if (ban == null)
			{
				await CloseRealtime(userID, null);
			}

			s_log.LogInformation("Revoked {Count} authorization(s) of user {UserId}: {Reason}", revoked.Count, userID, reason);
			return revoked.Count;
		}

		// Closes the user's realtime connections on every copy with auth.revoked, all or one session type
		public static Task CloseRealtime(Int64 userID, EUserSessionType? onlySessionType)
		{
			return Realtime.RealtimeControl.RequestClose(Realtime.CloseRequest.ForUser(userID, AuthRevokedCode, allowReconnect: false, notice: null, onlySessionType));
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

			// a name reset ends what the access token still allows within one reconcile tick, CLI resets included
			if (AccessControl.MustChooseName(userID))
			{
				s_log.LogInformation("Refused access token of user {UserId}, who must choose a new name", userID);
				return AuthenticateResult.Fail("name required");
			}

			PlayBan? ban = AccessControl.GetPlayBan(userID);
			if (ban != null)
			{
				Context.Items[BanItemKey] = ban;
				return AuthenticateResult.Fail("banned");
			}

			string clientID = token.GetClaim(OpenIddictConstants.Claims.ClientId) ?? token.GetClaim(OpenIddictConstants.Claims.AuthorizedParty) ?? String.Empty;

			// a disabled or removed client's tokens stop at once, before their authorizations are revoked
			RegisteredClient? client = ClientRegistry.FindEnabled(clientID);
			if (client == null)
			{
				s_log.LogInformation("Refused access token of unregistered or disabled client {ClientId}", clientID);
				return AuthenticateResult.Fail("client disabled");
			}

			var claims = new List<Claim>
			{
				new Claim(ClaimTypes.NameIdentifier, userID.ToString()),
				new Claim(JwtRegisteredClaimNames.Name, token.GetClaim(OpenIddictConstants.Claims.Name) ?? String.Empty),
				new Claim(JwtRegisteredClaimNames.Address, IPHelpers.NormalizeIP(Context.Connection.RemoteIpAddress?.ToString())),
				new Claim(ClientIDClaim, clientID),
				new Claim(AuthorizationIDClaim, token.GetClaim(OpenIddictConstants.Claims.Private.AuthorizationId) ?? String.Empty)
			};

			// game roles come from the registered client's kind and the game scope, never from the token itself
			EUserSessionType sessionType = client.SessionType;
			if (token.HasScope(AccountsSettings.GameScope) && sessionType != EUserSessionType.None)
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

				// one body for the upgrade and REST; a permanent ban has no expires_at
				var parameters = new Dictionary<string, string> { ["reason"] = ban.Reason };
				if (ban.ExpiresAt != null)
				{
					parameters["expires_at"] = ban.ExpiresAt.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
				}

				await Response.WriteAsJsonAsync(new { error = "moderation.banned", @params = parameters });

				return;
			}

			await UniformAuthResponses.WriteUnauthorized(Context);
		}
	}
}
