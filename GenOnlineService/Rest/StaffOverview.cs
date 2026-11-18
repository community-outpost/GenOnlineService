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
using GenOnlineService.OpenApi;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GenOnlineService.Rest
{
	// The unreviewed rows of one anti-cheat review list
	public sealed record StaffOverviewQueue
	{
		public string Kind { get; init; } = "";
		public int Unreviewed { get; init; }
	}

	// A kick, mute or ban of the activity feed, with the player it hit
	public sealed record StaffOverviewSanction
	{
		public long Id { get; init; }
		public string Type { get; init; } = "";
		public Member Player { get; init; } = new Member();
		public string? Reason { get; init; }
		public DateTime CreatedAt { get; init; }
		public DateTime? ExpiresAt { get; init; }
		public DateTime? LiftedAt { get; init; }
		public Member? CreatedBy { get; init; }
		public bool Active { get; init; }
	}

	// The numbers and latest rows of the staff start page; the anti-cheat parts are null without anticheat.review
	public sealed record StaffOverviewResponse
	{
		public int PlayersOnline { get; init; }
		public int LobbiesOpen { get; init; }
		public int MatchesToday { get; init; }
		public int OpenReports { get; init; }
		public int? UnreviewedAnticheat { get; init; }
		public IReadOnlyList<StaffOverviewQueue>? Anticheat { get; init; }
		public IReadOnlyList<ReportInfo> RecentReports { get; init; } = [];
		public IReadOnlyList<StaffOverviewSanction> RecentSanctions { get; init; } = [];
	}

	// The read behind the staff dashboard, GET /api/v1/staff/overview
	public static class StaffOverview
	{
		// The activity feed shows this many of the newest reports and sanctions
		public const int RecentCount = 5;

		// playersOnline and lobbiesOpen are this process's live counts, the rest comes from the database.
		// Matches today are the ones finished since 00:00 UTC, the day of the faction numbers of /api/v1/stats/global,
		// counted on the finished_at index rather than a scan of every match ever started.
		public static async Task<StaffOverviewResponse> GetAsync(IDbContextFactory<AppDbContext> dbFactory, int playersOnline, int lobbiesOpen, bool canReviewAntiCheat, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			DateTime dayStart = DateTime.SpecifyKind(utcNow.Date, DateTimeKind.Utc);
			int matchesToday = await db.Matches.AsNoTracking().CountAsync(m => m.FinishedAt != null && m.FinishedAt >= dayStart, cancellationToken);

			(int openReports, _) = await Database.Chat.GetReportsPage(db, [PlayerReportStatuses.Open], null, true, 0, 0, cancellationToken);
			(_, List<PlayerReport> reports) = await Database.Chat.GetReportsPage(db, null, null, true, 0, RecentCount, cancellationToken);

			// the primary key orders by creation without an index on created_at
			List<Sanction> sanctions = await db.Sanctions.AsNoTracking().OrderByDescending(s => s.ID).Take(RecentCount).ToListAsync(cancellationToken);

			List<StaffOverviewQueue>? queues = null;
			if (canReviewAntiCheat)
			{
				queues = new List<StaffOverviewQueue>();
				foreach (string kind in AntiCheatReviewKinds.All)
				{
					(int unreviewed, _) = await Database.AntiCheatReviews.List(db, kind, false, null, 0, 0, cancellationToken);
					queues.Add(new StaffOverviewQueue { Kind = kind, Unreviewed = unreviewed });
				}
			}

			HashSet<long> userIDs = reports.SelectMany(ReportUserIDs)
				.Concat(sanctions.Select(s => s.UserID))
				.Concat(sanctions.Where(s => s.CreatedByID != null).Select(s => s.CreatedByID!.Value))
				.ToHashSet();
			Dictionary<long, (string Name, int Tag)> names = await Database.DisplayNames.GetBulk(db, userIDs);

			return new StaffOverviewResponse
			{
				PlayersOnline = playersOnline,
				LobbiesOpen = lobbiesOpen,
				MatchesToday = matchesToday,
				OpenReports = openReports,
				UnreviewedAnticheat = queues?.Sum(q => q.Unreviewed),
				Anticheat = queues,
				RecentReports = reports.Select(r => ReportOf(r, names)).ToList(),
				RecentSanctions = sanctions.Select(s => new StaffOverviewSanction
				{
					Id = s.ID,
					Type = s.Type,
					Player = RealtimeMembers.Of(s.UserID, names),
					Reason = String.IsNullOrEmpty(s.Reason) ? null : s.Reason,
					CreatedAt = s.CreatedAt,
					ExpiresAt = s.Type == SanctionTypes.Kick ? null : s.ExpiresAt,
					LiftedAt = s.LiftedAt,
					CreatedBy = s.CreatedByID is long creator ? RealtimeMembers.Of(creator, names) : null,
					// a kick ends as it starts, so it is never active
					Active = s.Type != SanctionTypes.Kick && s.LiftedAt == null && (s.ExpiresAt == null || s.ExpiresAt > utcNow)
				}).ToList()
			};
		}

		// A report row in the fields of GET /api/v1/reports
		public static ReportInfo ReportOf(PlayerReport report, IReadOnlyDictionary<long, (string Name, int Tag)> names) => new ReportInfo
		{
			Id = report.ID,
			Reporter = RealtimeMembers.Of(report.ReporterID, names),
			Reported = RealtimeMembers.Of(report.ReportedUserID, names),
			MessageId = report.MessageID?.ToString(CultureInfo.InvariantCulture),
			Reason = report.Reason,
			Status = report.Status,
			CreatedAt = report.CreatedAt,
			ResolvedAt = report.ResolvedAt,
			ResolvedByKind = report.ResolvedByKind,
			ResolvedBy = report.ResolvedByKind == AuditActorKinds.User && report.ResolvedByID is long resolver ? RealtimeMembers.Of(resolver, names) : null,
			ResolutionNote = report.ResolutionNote
		};

		// The players a report row names: both sides and the staff member who closed it
		public static IEnumerable<long> ReportUserIDs(PlayerReport report)
		{
			yield return report.ReporterID;
			yield return report.ReportedUserID;
			if (report.ResolvedByKind == AuditActorKinds.User && report.ResolvedByID is long resolver)
			{
				yield return resolver;
			}
		}
	}

	// The staff dashboard of the /admin pages: any staff permission, with the sign-in cookie (T16) or a token; the
	// anti-cheat numbers need anticheat.review, as the review lists do. A cookie answer is never cached (ApiAuthentication)
	[ApiController]
	[Authorize]
	public sealed class StaffOverviewController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly IAuthorizationService m_authorization;

		public StaffOverviewController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization)
		{
			m_dbFactory = dbFactory;
			m_authorization = authorization;
		}

		[AllowSignInCookie]
		[EndpointSummary("Reads the counts of the staff overview page.")]
		[ProducesResponseType<StaffOverviewResponse>(StatusCodes.Status200OK)]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
		[HttpGet("~/api/v1/staff/overview")]
		public async Task<IActionResult> Get()
		{
			if (!await StaffAccess.IsStaffAsync(m_authorization, User))
			{
				return RestResults.Fail(Result.Forbidden(Permissions.ModerationKick));
			}

			bool antiCheat = (await m_authorization.AuthorizeAsync(User, PermissionPolicies.For(Permissions.AnticheatReview))).Succeeded;
			// a host without the lobby side (the in-process test pipeline) has no live lobbies to count
			int lobbies = HttpContext.RequestServices.GetService<LobbyManager>()?.GetNumLobbies() ?? 0;
			return RestResults.Ok(await StaffOverview.GetAsync(m_dbFactory, WebSocketManager.GetNumberOfUsersOnline(), lobbies, antiCheat, DateTime.UtcNow, HttpContext.RequestAborted));
		}
	}
}
