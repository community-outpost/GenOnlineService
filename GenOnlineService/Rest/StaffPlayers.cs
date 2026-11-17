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
using GenOnlineService.Portal.Client;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;

namespace GenOnlineService.Rest
{
	// An account as staff find it: blocked and deactivated accounts are listed with their state
	public sealed record StaffPlayer
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public string? Tag { get; init; }
		public string State { get; init; } = "";
		public DateTime CreatedAt { get; init; }
		public DateTime? LastSignInAt { get; init; }
	}

	// GET /api/v1/players/search
	public sealed record StaffPlayerListResponse
	{
		public IReadOnlyList<StaffPlayer> Players { get; init; } = [];
	}

	// moderation.kick or higher, the rule of the report queue
	public static class StaffAccess
	{
		private static readonly string[] s_permissions = [Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan];

		public static async Task<bool> IsStaffAsync(IAuthorizationService authorization, ClaimsPrincipal user)
		{
			foreach (string permission in s_permissions)
			{
				if ((await authorization.AuthorizeAsync(user, PermissionPolicies.For(permission))).Succeeded)
				{
					return true;
				}
			}
			return false;
		}
	}

	public static class StaffPlayerActions
	{
		public const int MinSearchLength = 2;
		public const int MaxSearchLength = User.UsernameMaxLength;

		// One audit row per staff member and player inside this window
		private static readonly TimeSpan s_inspectionAuditWindow = TimeSpan.FromHours(1);

		// The providers with a public profile page, keyed by the stored provider name
		private static readonly Dictionary<string, string> s_profileUrls = new(StringComparer.Ordinal)
		{
			[IdentityProviders.Steam] = "https://steamcommunity.com/profiles/",
			[IdentityProviders.Discord] = "https://discord.com/users/"
		};

		private static readonly (EUserSessionType SessionType, string Kind)[] s_sessionKinds =
		{
			(EUserSessionType.GameClient, "game"),
			(EUserSessionType.GameLauncher, "launcher"),
			(EUserSessionType.ChatClient, "chat")
		};

		// A part of a name of 2 to 32 characters, or a player id of any length
		public static (string? Search, Failure? Failure) ReadSearch(string? text)
		{
			string search = text?.Trim() ?? String.Empty;
			bool id = long.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value > 0;
			if (search.Length > MaxSearchLength || (search.Length < MinSearchLength && !id))
			{
				return (null, Result.Invalid("query"));
			}
			return (search, null);
		}

		public static async Task<(int Total, StaffPlayerListResponse Response)> SearchAsync(IDbContextFactory<AppDbContext> dbFactory, string search, int skip, int take, CancellationToken cancellationToken = default)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			(int total, List<Database.StaffPlayerRow> rows) = await Database.Users.StaffSearch(db, search, skip, take, cancellationToken);
			return (total, new StaffPlayerListResponse
			{
				Players = rows.Select(row => new StaffPlayer
				{
					Id = row.ID,
					Name = row.Name,
					Tag = PlayerTags.Of(row.NameTag),
					State = row.State,
					CreatedAt = row.CreatedAt,
					LastSignInAt = row.LastSignInAt
				}).ToArray()
			});
		}

		// The account with its sessions, ratings and sanctions, nothing here is personal data beyond the public profile
		public static async Task<Result<PortalPlayer>> GetAsync(IDbContextFactory<AppDbContext> dbFactory, long userID, CancellationToken cancellationToken = default)
		{
			User? user;
			EloData elo;
			List<UserRole> roleRows;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
			{
				user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ID == userID, cancellationToken);
				if (user == null)
				{
					return Result.NotFound("user");
				}
				elo = (await Database.PlayerRatings.GetBulk(db, [userID])).GetValueOrDefault(userID) ?? new EloData(EloConfig.BaseRating, EloConfig.BaseRating, 0);
				roleRows = await Database.Moderation.GetRoles(db, userID);
			}

			Result<SanctionsResponse> sanctions = await SanctionActions.ListAsync(dbFactory, userID, activeOnly: false);
			if (!sanctions.Ok)
			{
				return new Failure(sanctions.Code!, sanctions.Params!);
			}

			var sessions = new List<PortalPlayerSession>();
			foreach ((EUserSessionType sessionType, string kind) in s_sessionKinds)
			{
				if (GameSessions.Get(userID, sessionType) is GameSessionRegistration registration)
				{
					sessions.Add(new PortalPlayerSession { Kind = kind, ClientId = registration.ClientID, RegisteredAt = registration.RegisteredAt });
				}
			}

			Int64 lobbyID = WebSocketManager.GetAllDataFromUser(userID).Select(session => session.currentLobbyID).FirstOrDefault(id => id != -1, -1);
			DateTime now = DateTime.UtcNow;
			return Result.Success(new PortalPlayer
			{
				Id = user.ID,
				Name = user.Username,
				Tag = PlayerTags.Of(user.NameTag),
				State = user.State,
				CreatedAt = user.CreatedAt,
				LastSignInAt = user.LastSignInAt,
				Roles = StaffRoles.RolesOf(userID).Order(StringComparer.Ordinal).ToList(),
				Online = WebSocketManager.GetSharedDataForUser(userID) != null,
				LobbyId = lobbyID == -1 ? null : lobbyID,
				Sessions = sessions,
				EloRating = elo.Rating,
				MonthlyEloRating = elo.MonthlyRating,
				EloMatches = elo.NumMatches,
				Sanctions = sanctions.Data!.Sanctions.Select(sanction => new PortalSanction
				{
					Id = sanction.Id,
					Type = sanction.Type.ToString().ToLowerInvariant(),
					Reason = sanction.Reason,
					CreatedAt = sanction.CreatedAt,
					ExpiresAt = sanction.ExpiresAt,
					LiftedAt = sanction.LiftedAt,
					CreatedBy = sanction.CreatedBy is Member by ? new PortalMember { Id = by.Id, Name = by.Name, Tag = by.Tag } : null,
					Active = sanction.Type != SanctionType.Kick && sanction.LiftedAt == null && (sanction.ExpiresAt == null || sanction.ExpiresAt > now)
				}).ToList(),
				RoleGrants = roleRows.Select(r => new PortalRoleGrant { Role = r.Role, Source = r.Source }).ToList(),
				// each environment owns its names since the identity hub, so staff can always rename here
				CanRename = true
			});
		}

		// Sign-ins, devices, addresses and possible duplicates, after one audit row an hour per staff member and player
		public static async Task<Result<PortalInspection>> InspectAsync(IDbContextFactory<AppDbContext> dbFactory, Database.ModerationActor actor, long userID, CancellationToken cancellationToken = default)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			if (!await Database.Users.Exists(db, userID))
			{
				return Result.NotFound("user");
			}

			// the audit row is written first, a failed write shows nothing
			await Database.Moderation.LogInspection(db, actor, userID, DateTime.UtcNow, s_inspectionAuditWindow);

			List<UserIdentity> identities = await db.Identities.AsNoTracking().Where(i => i.UserID == userID).OrderBy(i => i.Provider).ToListAsync(cancellationToken);
			Database.LastSession? last = await Database.StaffInspection.GetLastSession(db, userID, cancellationToken);
			List<Database.InspectedDevice> devices = await Database.StaffInspection.GetDevices(db, userID, cancellationToken);
			List<Database.InspectedAddress> addresses = await Database.StaffInspection.GetAddresses(db, userID, cancellationToken);
			List<Database.DuplicateAccount> duplicates = await Database.StaffInspection.GetDuplicates(db, userID, cancellationToken);

			return Result.Success(new PortalInspection
			{
				LastIp = last?.Ip,
				LastCountry = last?.Country,
				LastSeenAt = last?.At,
				Identities = identities.Select(i => new PortalIdentity
				{
					Provider = i.Provider,
					ExternalId = i.ExternUid,
					LinkedAt = i.CreatedAt,
					LastUsedAt = i.LastUsedAt,
					ProfileUrl = s_profileUrls.TryGetValue(i.Provider, out string? prefix) ? prefix + Uri.EscapeDataString(i.ExternUid) : null
				}).ToList(),
				Devices = devices.Select(d => new PortalDevice { Kind = d.Kind, Fingerprint = d.Fingerprint, FirstSeenAt = d.FirstSeenAt, LastSeenAt = d.LastSeenAt, LastIp = d.LastIp }).ToList(),
				Addresses = addresses.Select(a => new PortalAddress { Ip = a.Ip, Country = a.Country, FirstSeenAt = a.FirstSeenAt, LastSeenAt = a.LastSeenAt, Events = a.Events }).ToList(),
				Duplicates = duplicates.Select(d => new PortalDuplicate
				{
					Id = d.UserID,
					Name = d.Name,
					Tag = PlayerTags.Of(d.NameTag),
					State = d.State,
					Match = d.Match,
					Shared = d.Shared,
					LastSeenAt = d.LastSeenAt
				}).ToList()
			});
		}
	}

	// The staff search over every account, the public search of GET /api/v1/players matches a prefix of visible accounts only
	[ApiController]
	[Authorize]
	public sealed class StaffPlayersController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly IAuthorizationService m_authorization;

		public StaffPlayersController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization)
		{
			m_dbFactory = dbFactory;
			m_authorization = authorization;
		}

		[AllowSignInCookie]
		[HttpGet("~/api/v1/players/search")]
		public async Task<IActionResult> Search([FromQuery] string? query)
		{
			if (!await StaffAccess.IsStaffAsync(m_authorization, User))
			{
				return RestResults.Fail(Result.Forbidden(Permissions.ModerationKick));
			}

			(string? search, Failure? failure) = StaffPlayerActions.ReadSearch(query);
			if (failure == null)
			{
				(OffsetPage page, Failure? pageFailure) = Paging.ReadOffset(Request);
				if (pageFailure == null)
				{
					(int total, StaffPlayerListResponse response) = await StaffPlayerActions.SearchAsync(m_dbFactory, search!, page.Skip, page.PerPage, HttpContext.RequestAborted);
					Paging.WriteOffsetHeaders(HttpContext, page, total);
					return RestResults.Ok(response);
				}
				failure = pageFailure;
			}
			return RestResults.Fail(failure.Value);
		}

		// The player page of the staff pages: the account, its sessions, ratings and sanctions, for any staff permission
		[AllowSignInCookie]
		[HttpGet("~/api/v1/players/{userID:long}/staff_view")]
		public async Task<IActionResult> StaffView(long userID)
		{
			if (!await StaffAccess.IsStaffAsync(m_authorization, User))
			{
				return RestResults.Fail(Result.Forbidden(Permissions.ModerationKick));
			}
			Result<PortalPlayer> player = await StaffPlayerActions.GetAsync(m_dbFactory, userID, HttpContext.RequestAborted);
			if (player.Ok && (await m_authorization.AuthorizeAsync(User, PermissionPolicies.For(Permissions.RolesManage))).Succeeded)
			{
				// the roles this caller may change here, by the rank rule of the roles routes
				await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
				Database.ModerationActor actor = CallerActors.Of(User);
				IReadOnlyList<string> assignable = RoleRules.Assignable(actor, await RoleRules.CallerRankAsync(db, actor), userID, await RoleRules.RankAsync(db, userID));
				player = Result.Success(player.Data! with { AssignableRoles = assignable });
			}
			return ApiResults.Answer(player);
		}

		// The personal data of a player, players.inspect and audited before it is read
		[AllowSignInCookie]
		[Authorize(Policy = PermissionPolicies.Prefix + Permissions.PlayersInspect)]
		[HttpGet("~/api/v1/players/{userID:long}/inspection")]
		public async Task<IActionResult> Inspect(long userID)
		{
			return ApiResults.Answer(await StaffPlayerActions.InspectAsync(m_dbFactory, CallerActors.Of(User), userID, HttpContext.RequestAborted));
		}
	}
}
