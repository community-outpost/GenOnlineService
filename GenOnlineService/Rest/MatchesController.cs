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

using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GenOnlineService.Rest
{
	// ?after_id=&since=&per_page= of GET /api/v1/matches
	public readonly record struct MatchListArgs(long? AfterID, DateTime? Since, int PerPage)
	{
		// Without after_id and since the page holds the newest matches, highest id first
		public bool Newest => AfterID == null && Since == null;
	}

	public static class MatchArgs
	{
		public static (MatchListArgs Args, Failure? Failure) Read(IQueryCollection query)
		{
			long? afterID = null;
			DateTime? since = null;
			int perPage = OffsetPage.DefaultPerPage;

			if (query.TryGetValue("after_id", out var afterText))
			{
				if (!long.TryParse(afterText, NumberStyles.None, CultureInfo.InvariantCulture, out long after))
				{
					return (default, Result.Invalid("after_id"));
				}
				afterID = after;
			}

			if (query.TryGetValue("since", out var sinceText))
			{
				// ISO 8601 with a time; a time without Z or an offset is read as UTC
				string text = sinceText.ToString();
				if (!text.Contains('T') || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
				{
					return (default, Result.Invalid("since"));
				}
				since = parsed.UtcDateTime;
			}

			if (query.TryGetValue("per_page", out var perPageText) && (!int.TryParse(perPageText, NumberStyles.None, CultureInfo.InvariantCulture, out perPage) || perPage < 1 || perPage > OffsetPage.MaxPerPage))
			{
				return (default, Result.Invalid("per_page"));
			}

			return (new MatchListArgs(afterID, since, perPage), null);
		}
	}

	// Match history for operators and stats sites, as the legacy MatchHistory routes
	[ApiController]
	[Authorize(Policy = PermissionPolicies.Prefix + Permissions.MatchHistoryRead)]
	public sealed class MatchesController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public MatchesController(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		// Keyset paged finished matches. With after_id or since: matches with an id above after_id that finished at or after since,
		// lowest id first, and a Link rel=next with after_id=<last id> when the page is full. With neither: the newest page,
		// highest id first and without a Link; a reader continues forward with after_id=<highest id of that page>.
		[HttpGet("~/api/v1/matches")]
		public async Task<IActionResult> List()
		{
			(MatchListArgs args, Failure? failure) = MatchArgs.Read(Request.Query);
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
			List<MatchHistory_Entry> entries = await Database.MatchHistory.GetMatchesPage(db, args.AfterID, args.Since, args.PerPage, HttpContext.RequestAborted);

			if (!args.Newest && entries.Count == args.PerPage)
			{
				Paging.WriteKeysetNext(HttpContext, "after_id", entries[^1].match_id);
			}
			IReadOnlyDictionary<long, int> tags = await TagsOf(db, entries.SelectMany(MatchResponse.UserIdsOf));
			return RestResults.Ok(new MatchListResponse { Matches = entries.Select(entry => MatchResponse.Of(entry, tags)).ToArray() });
		}

		// One match, finished or running
		[HttpGet("~/api/v1/matches/{matchID:long}")]
		public async Task<IActionResult> Get(long matchID)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
			MatchHistory_Entry? entry = await Database.MatchHistory.LoadMatchHistoryEntryAsync(db, matchID, HttpContext.RequestAborted);
			return entry == null ? RestResults.NotFound("match") : RestResults.Ok(MatchResponse.Of(entry, await TagsOf(db, MatchResponse.UserIdsOf(entry))));
		}

		private static async Task<IReadOnlyDictionary<long, int>> TagsOf(AppDbContext db, IEnumerable<long> userIDs)
		{
			Dictionary<long, (string Name, int Tag)> names = await Database.DisplayNames.GetBulk(db, userIDs.ToHashSet());
			return names.ToDictionary(pair => pair.Key, pair => pair.Value.Tag);
		}
	}
}
