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
	// One player of a listed match; name is the one the player had in the match, tag the account's current tag
	public sealed record StaffMatchPlayerInfo
	{
		public long UserId { get; init; }
		public string Name { get; init; } = "";
		public string? Tag { get; init; }
		public int? Team { get; init; }
		public int? Side { get; init; }
		public bool Won { get; init; }
		public bool Desynced { get; init; }
	}

	// One match of GET /api/v1/matches/search: type is null for a match the old history kept without one, finished_at and
	// duration_s are null while it runs; flags are desync, no_result (finished without a winner) and, with anticheat.review,
	// anticheat (an unreviewed anti-cheat row)
	public sealed record StaffMatchSummary
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public LobbyType? Type { get; init; }
		public MapRef Map { get; init; } = new MapRef();
		public DateTime StartedAt { get; init; }
		public DateTime? FinishedAt { get; init; }
		public long? DurationS { get; init; }
		public IReadOnlyList<StaffMatchPlayerInfo> Players { get; init; } = [];
		public IReadOnlyList<string> Flags { get; init; } = [];
	}

	public sealed record StaffMatchListResponse
	{
		public IReadOnlyList<StaffMatchSummary> Matches { get; init; } = [];
	}

	// ?type=&map=&player=&from=&to=&flagged= of GET /api/v1/matches/search, each one optional
	public sealed record StaffMatchListArgs
	{
		public ELobbyType? Type { get; init; }
		public string? Map { get; init; }
		public string? Player { get; init; }
		public DateTime? From { get; init; }
		public DateTime? To { get; init; }
		public bool Flagged { get; init; }
	}

	public static class StaffMatchActions
	{
		// A whole name of 32 characters with #0000
		public const int MaxPlayerLength = User.UsernameMaxLength + 5;

		public static (StaffMatchListArgs Args, Failure? Failure) ReadList(IQueryCollection query)
		{
			ELobbyType? type = null;
			string? typeText = Text(query, "type");
			if (typeText != null)
			{
				type = typeText switch
				{
					"quick_match" => ELobbyType.QuickMatch,
					"custom" => ELobbyType.CustomGame,
					_ => null
				};
				if (type == null)
				{
					return (new StaffMatchListArgs(), Result.Invalid("type"));
				}
			}

			string? map = Text(query, "map");
			if (map != null && map.Length > global::Match.MapNameMaxLength)
			{
				return (new StaffMatchListArgs(), Result.Invalid("map"));
			}

			string? player = Text(query, "player");
			if (player != null && player.Length > MaxPlayerLength)
			{
				return (new StaffMatchListArgs(), Result.Invalid("player"));
			}

			(DateTime? from, bool fromValid) = ReadTime(Text(query, "from"), endOfDay: false);
			if (!fromValid)
			{
				return (new StaffMatchListArgs(), Result.Invalid("from"));
			}
			(DateTime? to, bool toValid) = ReadTime(Text(query, "to"), endOfDay: true);
			if (!toValid)
			{
				return (new StaffMatchListArgs(), Result.Invalid("to"));
			}

			bool flagged = false;
			string? flaggedText = Text(query, "flagged");
			if (flaggedText != null && !Boolean.TryParse(flaggedText, out flagged))
			{
				return (new StaffMatchListArgs(), Result.Invalid("flagged"));
			}

			return (new StaffMatchListArgs { Type = type, Map = map, Player = player, From = from, To = to, Flagged = flagged }, null);
		}

		// The trimmed first value, null when absent or empty
		private static string? Text(IQueryCollection query, string name) =>
			StaffPlayerActions.First(query, name)?.Trim() is { Length: > 0 } text ? text : null;

		// A day (yyyy-MM-dd, the whole UTC day: from its start, or to its end) or an ISO 8601 time; a time without Z or an offset is UTC.
		// "to" the last day of the calendar has no next day to stop before, so it leaves the list open-ended.
		private static (DateTime? Time, bool Valid) ReadTime(string? text, bool endOfDay)
		{
			if (text == null)
			{
				return (null, true);
			}
			if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime day))
			{
				if (endOfDay && day.Date == DateTime.MaxValue.Date)
				{
					return (null, true);
				}
				return (DateTime.SpecifyKind(endOfDay ? day.Date.AddDays(1) : day.Date, DateTimeKind.Utc), true);
			}
			if (text.Contains('T') && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time))
			{
				return (time.UtcDateTime, true);
			}
			return (null, false);
		}

		// antiCheat is whether the caller may review anti-cheat rows; a player filter that names nobody lists nothing
		public static async Task<(Database.StaffMatchList List, StaffMatchListResponse Response)> ListAsync(IDbContextFactory<AppDbContext> dbFactory, StaffMatchListArgs args, bool antiCheat, int skip, int take, CancellationToken cancellationToken = default)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
			List<long>? playerIDs = args.Player == null ? null : await Database.StaffMatchLists.PlayersNamed(db, args.Player, cancellationToken);
			Database.StaffMatchList list = await Database.StaffMatchLists.ListAsync(db, new Database.StaffMatchQuery
			{
				Type = args.Type,
				Map = args.Map,
				PlayerIDs = playerIDs,
				From = args.From,
				To = args.To,
				Flagged = args.Flagged,
				AntiCheat = antiCheat
			}, skip, take, cancellationToken: cancellationToken);

			return (list, new StaffMatchListResponse
			{
				Matches = list.Matches.Select(match => new StaffMatchSummary
				{
					Id = match.Id,
					Name = match.Name,
					Type = match.Type switch
					{
						ELobbyType.QuickMatch => LobbyType.QuickMatch,
						ELobbyType.CustomGame => LobbyType.Custom,
						_ => null
					},
					Map = new MapRef { Name = match.Map, Path = match.MapPath ?? "", Official = match.OfficialMap },
					StartedAt = match.StartedAt,
					FinishedAt = match.FinishedAt,
					DurationS = match.FinishedAt is DateTime finished ? (long)(finished - match.StartedAt).TotalSeconds : null,
					Players = match.Players.Select(player => new StaffMatchPlayerInfo
					{
						UserId = player.UserId,
						Name = player.Name,
						Tag = player.Tag is int tag ? PlayerTags.Of(tag) : null,
						Team = player.Team,
						Side = player.Side,
						Won = player.Won,
						Desynced = player.Desynced
					}).ToArray(),
					Flags = match.Flags
				}).ToArray()
			});
		}
	}

	// The staff list of matches (T17f): any staff permission, like the staff player search, with the sign-in cookie (T16)
	[ApiController]
	[Authorize]
	public sealed class StaffMatchesController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly IAuthorizationService m_authorization;

		public StaffMatchesController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization)
		{
			m_dbFactory = dbFactory;
			m_authorization = authorization;
		}

		// Running and finished matches, newest first, offset paged; past 10,000 rows the answer carries no X-Total
		[AllowSignInCookie]
		[EndpointSummary("Searches matches for the staff pages.")]
		[ProducesResponseType<StaffMatchListResponse>(StatusCodes.Status200OK)]
		[ApiPermission(Permissions.ModerationKick, Permissions.ModerationMute, Permissions.ModerationBan)]
		[HttpGet("~/api/v1/matches/search")]
		public async Task<IActionResult> Search()
		{
			if (!await StaffAccess.IsStaffAsync(m_authorization, User))
			{
				return RestResults.Fail(Result.Forbidden(Permissions.ModerationKick));
			}

			(StaffMatchListArgs args, Failure? failure) = StaffMatchActions.ReadList(Request.Query);
			if (failure == null)
			{
				(OffsetPage page, Failure? pageFailure) = Paging.ReadOffset(Request);
				if (pageFailure == null)
				{
					bool antiCheat = (await m_authorization.AuthorizeAsync(User, PermissionPolicies.For(Permissions.AnticheatReview))).Succeeded;
					(Database.StaffMatchList list, StaffMatchListResponse response) = await StaffMatchActions.ListAsync(m_dbFactory, args, antiCheat, page.Skip, page.PerPage, HttpContext.RequestAborted);
					if (list.Total is int total)
					{
						Paging.WriteOffsetHeaders(HttpContext, page, total);
					}
					else
					{
						Paging.WriteOffsetHeaders(HttpContext, page, list.HasNext);
					}
					return RestResults.Ok(response);
				}
				failure = pageFailure;
			}
			return RestResults.Fail(failure.Value);
		}
	}
}
