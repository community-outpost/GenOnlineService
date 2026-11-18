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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenOnlineService.Rest
{
	// One row of a review list, match_id is null for a report raised at sign-in
	public sealed record AntiCheatReport
	{
		public long Id { get; init; }
		public string Kind { get; init; } = "";
		public Member Player { get; init; } = new Member();
		public long? MatchId { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? ModuleName { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? ModulePath { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public long? ModuleSize { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Reason { get; init; }

		// quickmatch_rules: the lobby, its playlist, the hub method and the sides it asked for, and when
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public long? LobbyId { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? PlaylistId { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? Method { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public IReadOnlyList<int>? Sides { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public DateTime? ReportedAt { get; init; }

		public DateTime? ReviewedAt { get; init; }
		public long? ReviewedBy { get; init; }
	}

	// GET /api/v1/anticheat_reports/:kind
	public sealed record AntiCheatReportsResponse
	{
		public IReadOnlyList<AntiCheatReport> Reports { get; init; } = [];
	}

	// PATCH /api/v1/anticheat_reports/:kind/:id
	public sealed record AntiCheatReviewRequest
	{
		public bool? Reviewed { get; init; }
	}

	// ?reviewed=&user_id= of the review lists, Reviewed null lists both states
	public readonly record struct AntiCheatListArgs(bool? Reviewed, long? UserID);

	public static class AntiCheatActions
	{
		// reviewed is false (default), true or all, user_id narrows the list to one player
		public static (AntiCheatListArgs Args, Failure? Failure) ReadList(IQueryCollection query)
		{
			bool? reviewed = false;
			if (query.TryGetValue("reviewed", out var reviewedText))
			{
				string text = reviewedText.ToString();
				if (text == "all")
				{
					reviewed = null;
				}
				else if (text == "true" || text == "false")
				{
					reviewed = text == "true";
				}
				else
				{
					return (default, Result.Invalid("reviewed"));
				}
			}

			long? userID = null;
			if (query.TryGetValue("user_id", out var userText))
			{
				if (!long.TryParse(userText, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id < 1)
				{
					return (default, Result.Invalid("user_id"));
				}
				userID = id;
			}
			return (new AntiCheatListArgs(reviewed, userID), null);
		}

		// One page of a list, newest first, with the current names of its players
		public static async Task<(int Total, AntiCheatReportsResponse? Response, Failure? Failure)> ListAsync(IDbContextFactory<AppDbContext> dbFactory, string kind, AntiCheatListArgs args, int skip, int take, CancellationToken cancellationToken = default)
		{
			if (!AntiCheatReviewKinds.IsKnown(kind))
			{
				return (0, null, Result.Invalid("kind"));
			}

			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			(int total, List<Database.AntiCheatReviewRow> rows) = await Database.AntiCheatReviews.List(db, kind, args.Reviewed, args.UserID, skip, take, cancellationToken);
			Dictionary<long, (string Name, int Tag)> names = await Database.DisplayNames.GetBulk(db, rows.Select(row => row.UserID).ToHashSet());

			return (total, new AntiCheatReportsResponse
			{
				Reports = rows.Select(row => new AntiCheatReport
				{
					Id = row.ReportID,
					Kind = kind,
					Player = RealtimeMembers.Of(row.UserID, names),
					MatchId = row.MatchID > 0 ? row.MatchID : null,
					ModuleName = row.ModuleName,
					ModulePath = row.ModulePath,
					ModuleSize = row.ModuleSize,
					Reason = row.Reason,
					LobbyId = row.LobbyID,
					PlaylistId = row.PlaylistID,
					Method = row.Method,
					Sides = SidesOf(row.Sides),
					ReportedAt = row.ReportedAt,
					ReviewedAt = row.ReviewedAt,
					ReviewedBy = row.ReviewedBy
				}).ToArray()
			}, null);
		}

		// The comma separated sides of a quickmatch_rules row, null for the other kinds
		private static IReadOnlyList<int>? SidesOf(string? sides)
		{
			if (sides == null)
			{
				return null;
			}
			return sides.Split(',', StringSplitOptions.RemoveEmptyEntries)
				.Select(text => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int side) ? (int?)side : null)
				.OfType<int>().ToArray();
		}

		// Marks one row reviewed with an audit row, a row reviewed before stays as it is and succeeds
		public static async Task<Result<Empty>> MarkReviewedAsync(IDbContextFactory<AppDbContext> dbFactory, Database.ModerationActor actor, string kind, long reportID, AntiCheatReviewRequest? request)
		{
			if (!AntiCheatReviewKinds.IsKnown(kind))
			{
				return Result.Invalid("kind");
			}
			if (request?.Reviewed != true)
			{
				return Result.Invalid("reviewed");
			}

			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			Database.AntiCheatReviewRow? row = reportID > 0 ? await Database.AntiCheatReviews.Get(db, kind, reportID) : null;
			if (row == null)
			{
				return Result.NotFound("report");
			}

			if (await Database.AntiCheatReviews.MarkReviewed(db, kind, reportID, actor.UserID, DateTime.UtcNow) == Database.AntiCheatMarkResult.Marked)
			{
				await Database.Moderation.LogAction(db, actor, ModerationActions.AnticheatReview, AuditTargetKinds.AnticheatReport, reportID, null, new { kind, user_id = row.UserID, match_id = row.MatchID });
			}
			return Result.Success();
		}
	}

	// The anti-cheat review lists: new accounts in quickmatch, unrecognized modules, suspicious probes and device values, illegal quick match requests
	[ApiController]
	[AllowSignInCookie]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.AnticheatReview)]
	public sealed class AntiCheatReportsController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public AntiCheatReportsController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		[EndpointSummary("Lists anti-cheat reports of one kind with paging.")]
		[ProducesResponseType<AntiCheatReportsResponse>(StatusCodes.Status200OK)]
		[HttpGet("~/api/v1/anticheat_reports/{kind}")]
		public async Task<IActionResult> List(string kind)
		{
			(AntiCheatListArgs args, Failure? failure) = AntiCheatActions.ReadList(Request.Query);
			if (failure == null)
			{
				(OffsetPage page, Failure? pageFailure) = Paging.ReadOffset(Request);
				if (pageFailure == null)
				{
					(int total, AntiCheatReportsResponse? response, Failure? listFailure) = await AntiCheatActions.ListAsync(m_dbFactory, kind, args, page.Skip, page.PerPage, HttpContext.RequestAborted);
					if (listFailure == null)
					{
						Paging.WriteOffsetHeaders(HttpContext, page, total);
						return RestResults.Ok(response!);
					}
					failure = listFailure;
				}
				else
				{
					failure = pageFailure;
				}
			}
			return RestResults.Fail(failure.Value);
		}

		[EndpointSummary("Marks an anti-cheat report reviewed or not.")]
		[ProducesResponseType(StatusCodes.Status204NoContent)]
		[ApiRequestBody(typeof(AntiCheatReviewRequest))]
		[HttpPatch("~/api/v1/anticheat_reports/{kind}/{reportID:long}")]
		public async Task<IActionResult> Review(string kind, long reportID)
		{
			AntiCheatReviewRequest? body;
			try
			{
				body = await JsonSerializer.DeserializeAsync<AntiCheatReviewRequest>(Request.Body, ApiResults.Json, HttpContext.RequestAborted);
			}
			catch (JsonException)
			{
				body = null;
			}
			return ApiResults.NoContent(await AntiCheatActions.MarkReviewedAsync(m_dbFactory, CallerActors.Of(User), kind, reportID, body));
		}
	}
}
