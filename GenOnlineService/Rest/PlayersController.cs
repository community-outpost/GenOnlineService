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
using System.Globalization;

namespace GenOnlineService.Rest
{
	// ?online=&search= of GET /api/v1/players; Search is trimmed
	public readonly record struct PlayerListArgs(bool Online, string? Search);

	public static class PlayerArgs
	{
		public const int MinSearchLength = 2;
		public const int MaxIds = 100;

		// At least one filter: online=true or a search of 2 or more characters; there is no unfiltered list of every account
		public static (PlayerListArgs Args, Failure? Failure) ReadList(IQueryCollection query)
		{
			bool online = false;
			if (query.TryGetValue("online", out var onlineText) && !bool.TryParse(onlineText, out online))
			{
				return (default, Result.Invalid("online"));
			}

			string? search = query.TryGetValue("search", out var searchText) ? searchText.ToString().Trim() : null;
			if (search != null && search.Length < MinSearchLength)
			{
				return (default, Result.Invalid("search"));
			}
			if (!online && search == null)
			{
				return (default, Result.Invalid("search"));
			}
			return (new PlayerListArgs(online, search), null);
		}

		// A comma separated list of 1 to 100 positive ids; duplicates are dropped, the first position is kept
		public static (IReadOnlyList<long> Ids, Failure? Failure) ReadIds(string? text)
		{
			if (String.IsNullOrEmpty(text))
			{
				return ([], Result.Invalid("ids"));
			}

			List<long> ids = new();
			HashSet<long> seen = new();
			foreach (string part in text.Split(','))
			{
				if (!long.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id < 1)
				{
					return ([], Result.Invalid("ids"));
				}
				if (seen.Add(id))
				{
					ids.Add(id);
				}
			}
			return ids.Count > MaxIds ? ([], Result.Invalid("ids")) : (ids, null);
		}
	}

	// Other players as a signed-in player sees them
	public sealed class PlayersController : ApiControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public PlayersController(IDbContextFactory<AppDbContext> dbFactory, IAuthorizationService authorization) : base(authorization)
		{
			m_dbFactory = dbFactory;
		}

		// online=true lists connected players from the hub's sessions; search= matches the start of display names, ignoring case;
		// both together search the connected players. Sorted by name, offset paged with ?page=&per_page=.
		[HttpGet("~/api/v1/players")]
		public async Task<IActionResult> List()
		{
			(PlayerListArgs args, Failure? failure) = PlayerArgs.ReadList(Request.Query);
			if (failure == null)
			{
				(OffsetPage page, Failure? pageFailure) = Paging.ReadOffset(Request);
				if (pageFailure == null)
				{
					return args.Online ? ListOnline(args.Search, page) : await SearchAsync(args.Search!, page);
				}
				failure = pageFailure;
			}
			return RestResults.Fail(failure.Value);
		}

		private IActionResult ListOnline(string? search, OffsetPage page)
		{
			string? prefix = search == null ? null : NameNormalizer.Normalize(search);
			List<Member> online = OnlinePlayers()
				.Where(member => prefix == null || NameNormalizer.Normalize(member.Name).StartsWith(prefix, StringComparison.Ordinal))
				.OrderBy(member => NameNormalizer.Normalize(member.Name), StringComparer.Ordinal)
				.ThenBy(member => member.Id)
				.ToList();

			Paging.WriteOffsetHeaders(HttpContext, page, online.Count);
			return RestResults.Ok(new PlayerListResponse { Players = online.Skip(page.Skip).Take(page.PerPage).ToArray() });
		}

		// One member per connected user; with several sessions the game session names the client, then the launcher, then chat
		private static IEnumerable<Member> OnlinePlayers()
		{
			EUserSessionType[] priority = [EUserSessionType.GameClient, EUserSessionType.GameLauncher, EUserSessionType.ChatClient];
			var sessions = WebSocketManager.GetUserDataCache();
			HashSet<long> seen = new();
			foreach (EUserSessionType sessionType in priority)
			{
				if (!sessions.TryGetValue(sessionType, out var byUser))
				{
					continue;
				}
				foreach (long userID in byUser.Keys)
				{
					SharedUserData? shared = WebSocketManager.GetSharedDataForUser(userID);
					if (shared != null && seen.Add(userID))
					{
						yield return RealtimeMembers.Of(userID, shared.m_strDisplayName, RealtimeMembers.ClientOf(sessionType), shared.m_NameTag);
					}
				}
			}
		}

		private async Task<IActionResult> SearchAsync(string search, OffsetPage page)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
			(int total, List<Database.PublicPlayer> players) = await Database.PublicPlayers.Search(db, search, page.Skip, page.PerPage);

			Paging.WriteOffsetHeaders(HttpContext, page, total);
			return RestResults.Ok(new PlayerListResponse { Players = players.Select(player => RealtimeMembers.Of(player.ID, player.Name, null, player.NameTag)).ToArray() });
		}

		// Blocked and deactivated accounts answer not_found; ratings are the stored ones
		[HttpGet("~/api/v1/players/{userID:long}")]
		public async Task<IActionResult> Get(long userID)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
			Database.PublicPlayer? player = await Database.PublicPlayers.Get(db, userID);
			if (player == null)
			{
				return RestResults.NotFound("user");
			}

			Dictionary<long, EloData> ratings = await Database.PlayerRatings.GetBulk(db, [userID]);
			EloData elo = ratings.TryGetValue(userID, out EloData? stored) ? stored : new EloData(EloConfig.BaseRating, EloConfig.BaseRating, 0);
			return RestResults.Ok(new PlayerProfileResponse
			{
				Id = player.Value.ID,
				Name = player.Value.Name,
				Tag = PlayerTags.Of(player.Value.NameTag),
				Badges = RealtimeBadges.For(userID),
				CreatedAt = player.Value.CreatedAt,
				EloRating = elo.Rating,
				MonthlyEloRating = elo.MonthlyRating,
				EloMatches = elo.NumMatches
			});
		}

		// A connected player's stats come from the session cache, which holds updates not yet read back; others from the database
		[HttpGet("~/api/v1/players/{userID:long}/stats")]
		public async Task<IActionResult> GetStats(long userID)
		{
			if (WebSocketManager.GetSharedDataForUser(userID)?.GameStats is PlayerStats cached)
			{
				return RestResults.Ok(PlayerStatsResponse.Of(cached));
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
			if (await Database.PublicPlayers.Get(db, userID) == null)
			{
				return RestResults.NotFound("user");
			}
			return RestResults.Ok(PlayerStatsResponse.Of(await Database.UserStats.GetPlayerStats(db, userID)));
		}

		// Up to 100 players in one call: connected ones from the session cache, the rest in one bulk read; unknown ids are left out
		[HttpGet("~/api/v1/players/stats")]
		public async Task<IActionResult> GetStatsBatch([FromQuery] string? ids)
		{
			(IReadOnlyList<long> userIDs, Failure? failure) = PlayerArgs.ReadIds(ids);
			if (failure != null)
			{
				return RestResults.Fail(failure.Value);
			}

			Dictionary<long, PlayerStats> found = new();
			List<long> offline = new();
			foreach (long userID in userIDs)
			{
				if (WebSocketManager.GetSharedDataForUser(userID)?.GameStats is PlayerStats cached)
				{
					found[userID] = cached;
				}
				else
				{
					offline.Add(userID);
				}
			}

			if (offline.Count > 0)
			{
				await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(HttpContext.RequestAborted);
				foreach (KeyValuePair<long, PlayerStats> pair in await Database.UserStats.GetPlayerStatsBulk(db, offline))
				{
					found[pair.Key] = pair.Value;
				}
			}

			return RestResults.Ok(new PlayerStatsBatchResponse
			{
				Stats = userIDs.Where(found.ContainsKey).Select(userID => PlayerStatsResponse.Of(found[userID])).ToArray()
			});
		}
	}
}
