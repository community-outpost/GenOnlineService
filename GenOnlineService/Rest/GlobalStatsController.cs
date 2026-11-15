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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GenOnlineService.Rest
{
	// Matches and wins of one playable side in finished matches of the current UTC day
	public sealed record FactionStat
	{
		public int Side { get; init; }
		public int Matches { get; init; }
		public int Wins { get; init; }
	}

	[ApiFixture("get-stats-global.response")]
	public sealed record GlobalStatsResponse
	{
		public int Players { get; init; }
		public int Lobbies { get; init; }
		public IReadOnlyList<FactionStat> Factions { get; init; } = [];
	}

	public static class GlobalStats
	{
		public static IReadOnlyList<FactionStat> FactionsOf(FactionStatsStructure stats)
		{
			return Enumerable.Range(0, FactionStatsStructure.numSides)
				.Select(index => new FactionStat { Side = Database.FactionStats.FirstSide + index, Matches = stats.matches[index], Wins = stats.wins[index] })
				.ToList();
		}
	}

	// The launcher shows these before sign-in, so the route is anonymous
	[ApiController]
	[AllowAnonymous]
	public sealed class GlobalStatsController : ControllerBase
	{
		private readonly LobbyManager m_lobbyManager;

		public GlobalStatsController(LobbyManager lobbyManager)
		{
			m_lobbyManager = lobbyManager;
		}

		[HttpGet("~/api/v1/stats/global")]
		public IActionResult Get()
		{
			return RestResults.Ok(new GlobalStatsResponse
			{
				Players = WebSocketManager.GetNumberOfUsersOnline(),
				Lobbies = m_lobbyManager.GetNumLobbies(),
				Factions = GlobalStats.FactionsOf(Database.FactionStats.Current)
			});
		}
	}
}
