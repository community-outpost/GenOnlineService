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
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService.Rest
{
	public static class LeaderboardIds
	{
		public const string Elo = "elo";

		public static bool IsKnown(string id) => id == Elo;
	}

	public sealed record LeaderboardEntryView
	{
		public int Rank { get; init; }
		public long UserId { get; init; }
		public string Name { get; init; } = "";
		public int Rating { get; init; }
		public int Matches { get; init; }
	}

	// GET /api/v1/leaderboards/:id
	[ApiFixture("get-leaderboards.response")]
	public sealed record LeaderboardResponse
	{
		public string Id { get; init; } = "";
		public IReadOnlyList<LeaderboardEntryView> Entries { get; init; } = [];
	}

	// Public, so the website shows rankings without a session
	[ApiController]
	[AllowAnonymous]
	public sealed class LeaderboardsController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public LeaderboardsController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		// elo: all-time ELO of visible players with at least one rated match, highest first, ties by user id;
		// rank is the position in that order, offset paged with ?page=&per_page=
		[HttpGet("~/api/v1/leaderboards/{id}")]
		public async Task<IActionResult> Get(string id)
		{
			if (!LeaderboardIds.IsKnown(id))
			{
				return RestResults.NotFound("leaderboard");
			}

			(OffsetPage page, Failure? failure) = Paging.ReadOffset(Request);
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
			(int total, List<Database.RankedPlayer> ranked) = await Database.PlayerRatings.GetRanked(db, page.Skip, page.PerPage);

			Paging.WriteOffsetHeaders(HttpContext, page, total);
			return RestResults.Ok(new LeaderboardResponse
			{
				Id = id,
				Entries = ranked.Select((entry, index) => new LeaderboardEntryView
				{
					Rank = page.Skip + index + 1,
					UserId = entry.UserID,
					Name = entry.Name,
					Rating = entry.Rating,
					Matches = entry.Matches
				}).ToArray()
			});
		}
	}
}
