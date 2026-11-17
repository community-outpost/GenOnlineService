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
using GenOnlineService.Names;
using GenOnlineService.Portal.Client;
using GenOnlineService.Realtime;
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GenOnlineService.Portal
{
	// The cookie endpoints of the admin pages: players, matches and the anti-cheat review lists
	public static partial class PortalSetup
	{
		// One audit row per staff member and player inside this window
		private static readonly TimeSpan s_inspectionAuditWindow = TimeSpan.FromHours(1);

		// The providers with a public profile page, keyed by the stored provider name
		private static readonly Dictionary<string, string> s_profileUrls = new(StringComparer.Ordinal)
		{
			[IdentityProviders.Steam] = "https://steamcommunity.com/profiles/",
			[IdentityProviders.Discord] = "https://discord.com/users/"
		};

		private static void MapAdmin(RouteGroupBuilder admin)
		{
			admin.MapGet("/players", SearchPlayers);
			admin.MapGet("/players/{userID:long}", GetPlayer);
			admin.MapPatch("/players/{userID:long}", RenamePlayer);
			admin.MapPost("/players/{userID:long}/sanctions", CreateSanction);
			admin.MapDelete("/sanctions/{sanctionID:long}", LiftSanction);
			admin.MapGet("/players/{userID:long}/inspection", InspectPlayer);
			admin.MapGet("/matches/{matchID:long}", GetMatch);
			admin.MapGet("/anticheat/{kind}", ListAntiCheat);
			admin.MapPatch("/anticheat/{kind}/{reportID:long}", ReviewAntiCheat);
		}

		// The check of the perm:<permission> policies, as the REST controllers run it
		private static Func<string, Task<Failure?>> PermissionCheck(HttpContext context)
		{
			IAuthorizationService authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
			return async permission => (await authorization.AuthorizeAsync(context.User, PermissionPolicies.For(permission))).Succeeded ? null : Result.Forbidden(permission);
		}

		private static (int Start, int Count) PageOf(int? start, int? count)
		{
			return (Math.Max(0, start ?? 0), Math.Clamp(count ?? OffsetPage.DefaultPerPage, 1, MaximumPageSize));
		}

		// The same search as GET /api/v1/players/search
		private static async Task<IResult> SearchPlayers(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, string? search, int? start, int? count)
		{
			(string? term, Failure? failure) = StaffPlayerActions.ReadSearch(search);
			if (failure != null)
			{
				return Answer(failure.Value);
			}

			(int skip, int take) = PageOf(start, count);
			(int total, StaffPlayerListResponse response) = await StaffPlayerActions.SearchAsync(dbFactory, term!, skip, take, context.RequestAborted);
			return Results.Json(new { response.Players, Total = total }, ApiResults.Json);
		}

		// The account with its sessions, ratings and sanctions, nothing here is personal data beyond the public profile
		private static async Task<IResult> GetPlayer(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, long userID)
		{
			User? user;
			EloData elo;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync(context.RequestAborted))
			{
				user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ID == userID, context.RequestAborted);
				if (user == null)
				{
					return Answer(Result.NotFound("user"));
				}
				elo = (await Database.PlayerRatings.GetBulk(db, [userID])).GetValueOrDefault(userID) ?? new EloData(EloConfig.BaseRating, EloConfig.BaseRating, 0);
			}

			Result<SanctionsResponse> sanctions = await SanctionActions.ListAsync(dbFactory, userID, activeOnly: false);
			if (!sanctions.Ok)
			{
				return Answer(new Failure(sanctions.Code!, sanctions.Params!));
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
			return Json(new PortalPlayer
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
				// each environment owns its names since the identity hub, so staff can always rename here
				CanRename = true
			});
		}

		// The same action as PATCH /api/v1/players/:id
		private static async Task<IResult> RenamePlayer(HttpContext context, PlayerNames names, long userID)
		{
			if (await PermissionCheck(context)(Permissions.ModerationRename) is Failure denied)
			{
				return Answer(denied);
			}

			PortalRenameRequest? body = await ReadAsync<PortalRenameRequest>(context);
			return Answer(await names.RenameByStaffAsync(SanctionActions.ApiActorOf(RealtimeClaims.UserID(context.User)), userID, body?.Name, body?.Reason));
		}

		// The same action as POST /api/v1/players/:id/sanctions, the service records the signed-in staff member as the actor
		private static async Task<IResult> CreateSanction(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, long userID)
		{
			PortalSanctionRequest? body = await ReadAsync<PortalSanctionRequest>(context);
			var request = new CreateSanctionRequest
			{
				Type = Enum.TryParse(body?.Type, ignoreCase: true, out SanctionType type) && Enum.IsDefined(type) ? type : null,
				DurationS = body?.DurationS,
				Reason = body?.Reason
			};

			Result<CreateSanctionResponse> result = await SanctionActions.CreateAsync(dbFactory, RealtimeClaims.UserID(context.User), userID, request, PermissionCheck(context));
			return result.Ok ? Results.NoContent() : Answer(new Failure(result.Code!, result.Params!));
		}

		// The same action as DELETE /api/v1/sanctions/:id
		private static async Task<IResult> LiftSanction(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, long sanctionID)
		{
			Func<string, Task<Failure?>> require = PermissionCheck(context);
			if (await require(Permissions.ModerationMute) != null && await require(Permissions.ModerationBan) != null)
			{
				return Answer(Result.Forbidden(Permissions.ModerationMute));
			}

			PortalReasonRequest? body = await ReadAsync<PortalReasonRequest>(context);
			return Answer(await SanctionActions.LiftAsync(dbFactory, RealtimeClaims.UserID(context.User), sanctionID, body?.Reason, require));
		}

		// Sign-ins, devices, addresses and possible duplicates: players.inspect, and one audit row an hour per staff member and player
		private static async Task<IResult> InspectPlayer(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, long userID)
		{
			if (await PermissionCheck(context)(Permissions.PlayersInspect) is Failure denied)
			{
				return Answer(denied);
			}

			await using AppDbContext db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
			if (!await Database.Users.Exists(db, userID))
			{
				return Answer(Result.NotFound("user"));
			}

			// the audit row is written first, a failed write shows nothing
			await Database.Moderation.LogInspection(db, CallerActors.Of(context.User), userID, DateTime.UtcNow, s_inspectionAuditWindow);

			List<UserIdentity> identities = await db.Identities.AsNoTracking().Where(i => i.UserID == userID).OrderBy(i => i.Provider).ToListAsync(context.RequestAborted);
			Database.LastSession? last = await Database.StaffInspection.GetLastSession(db, userID, context.RequestAborted);
			List<Database.InspectedDevice> devices = await Database.StaffInspection.GetDevices(db, userID, context.RequestAborted);
			List<Database.InspectedAddress> addresses = await Database.StaffInspection.GetAddresses(db, userID, context.RequestAborted);
			List<Database.DuplicateAccount> duplicates = await Database.StaffInspection.GetDuplicates(db, userID, context.RequestAborted);

			return Json(new PortalInspection
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

		// The same answer as GET /api/v1/matches/:id
		private static async Task<IResult> GetMatch(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, long matchID)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
			MatchHistory_Entry? entry = await Database.MatchHistory.LoadMatchHistoryEntryAsync(db, matchID, context.RequestAborted);
			if (entry == null)
			{
				return Answer(Result.NotFound("match"));
			}

			Dictionary<long, (string Name, int Tag)> names = await Database.DisplayNames.GetBulk(db, MatchResponse.UserIdsOf(entry).ToHashSet());
			return Results.Json(MatchResponse.Of(entry, names.ToDictionary(pair => pair.Key, pair => pair.Value.Tag)), ApiResults.Json);
		}

		// The same list as GET /api/v1/anticheat_reports/:kind
		private static async Task<IResult> ListAntiCheat(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, string kind, int? start, int? count)
		{
			if (await PermissionCheck(context)(Permissions.AnticheatReview) is Failure denied)
			{
				return Answer(denied);
			}

			(AntiCheatListArgs args, Failure? failure) = AntiCheatActions.ReadList(context.Request.Query);
			if (failure != null)
			{
				return Answer(failure.Value);
			}

			(int skip, int take) = PageOf(start, count);
			(int total, AntiCheatReportsResponse? response, Failure? listFailure) = await AntiCheatActions.ListAsync(dbFactory, kind, args, skip, take, context.RequestAborted);
			return listFailure != null ? Answer(listFailure.Value) : Results.Json(new { response!.Reports, Total = total }, ApiResults.Json);
		}

		// The same action as PATCH /api/v1/anticheat_reports/:kind/:id
		private static async Task<IResult> ReviewAntiCheat(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, string kind, long reportID)
		{
			if (await PermissionCheck(context)(Permissions.AnticheatReview) is Failure denied)
			{
				return Answer(denied);
			}

			PortalReviewRequest? body = await ReadAsync<PortalReviewRequest>(context);
			return Answer(await AntiCheatActions.MarkReviewedAsync(dbFactory, CallerActors.Of(context.User), kind, reportID, new AntiCheatReviewRequest { Reviewed = body?.Reviewed }));
		}

		// One page of the staff queue, filtered, searched, ordered and cut in the database
		private static async Task<IResult> GetReports(HttpContext context, IDbContextFactory<AppDbContext> dbFactory, string? status, string? search, int? start, int? count, bool? newest)
		{
			string[] statuses = (status ?? PlayerReportStatuses.Open).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (statuses.Length == 0 || statuses.Any(s => s != "all" && s != PlayerReportStatuses.Open && s != PlayerReportStatuses.Actioned && s != PlayerReportStatuses.Dismissed))
			{
				return Answer(Result.Invalid("status"));
			}

			(int skip, int take) = PageOf(start, count);
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
			(int total, List<PlayerReport> rows) = await Database.Chat.GetReportsPage(db, statuses.Contains("all") ? null : statuses, search, newest == true, skip, take, context.RequestAborted);
			Dictionary<long, (string Name, int Tag)> names = await Database.DisplayNames.GetBulk(db, rows.SelectMany(r => new[] { r.ReporterID, r.ReportedUserID }).ToHashSet());

			// the fields of GET /api/v1/reports
			List<ReportInfo> reports = rows.Select(r => new ReportInfo
			{
				Id = r.ID,
				Reporter = RealtimeMembers.Of(r.ReporterID, names),
				Reported = RealtimeMembers.Of(r.ReportedUserID, names),
				MessageId = r.MessageID?.ToString(CultureInfo.InvariantCulture),
				Reason = r.Reason,
				Status = r.Status,
				CreatedAt = r.CreatedAt,
				ResolvedAt = r.ResolvedAt,
				ResolutionNote = r.ResolutionNote
			}).ToList();
			return Results.Json(new { Reports = reports, Total = total }, ApiResults.Json);
		}
	}
}
