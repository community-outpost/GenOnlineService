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
using System.Text.Json.Serialization;

namespace GenOnlineService.Rest
{
	// GET /api/v1/players: online members carry client, search results do not
	[ApiFixture("get-players.response")]
	public sealed record PlayerListResponse
	{
		public IReadOnlyList<Member> Players { get; init; } = [];
	}

	// GET /api/v1/players/:id: the public facts of an account
	[ApiFixture("get-players-item.response")]
	public sealed record PlayerProfileResponse
	{
		public long Id { get; init; }
		public string Name { get; init; } = "";
		public IReadOnlyList<string> Badges { get; init; } = [];
		public DateTime CreatedAt { get; init; }
		public int EloRating { get; init; }
		public int MonthlyEloRating { get; init; }
		public int EloMatches { get; init; }
	}

	// Per general counters, each list has one entry per general index
	public sealed record GeneralStats
	{
		public IReadOnlyList<int> Wins { get; init; } = [];
		public IReadOnlyList<int> Losses { get; init; } = [];
		public IReadOnlyList<int> Games { get; init; } = [];
		public IReadOnlyList<int> Duration { get; init; } = [];
		public IReadOnlyList<int> UnitsKilled { get; init; } = [];
		public IReadOnlyList<int> UnitsLost { get; init; } = [];
		public IReadOnlyList<int> UnitsBuilt { get; init; } = [];
		public IReadOnlyList<int> BuildingsKilled { get; init; } = [];
		public IReadOnlyList<int> BuildingsLost { get; init; } = [];
		public IReadOnlyList<int> BuildingsBuilt { get; init; } = [];
		public IReadOnlyList<int> Earnings { get; init; } = [];
		public IReadOnlyList<int> TechCaptured { get; init; } = [];
		public IReadOnlyList<int> Discons { get; init; } = [];
		public IReadOnlyList<int> Desyncs { get; init; } = [];
		public IReadOnlyList<int> Surrenders { get; init; } = [];
		[JsonPropertyName("games_of_2p")]
		public IReadOnlyList<int> GamesOf2p { get; init; } = [];
		[JsonPropertyName("games_of_3p")]
		public IReadOnlyList<int> GamesOf3p { get; init; } = [];
		[JsonPropertyName("games_of_4p")]
		public IReadOnlyList<int> GamesOf4p { get; init; } = [];
		[JsonPropertyName("games_of_5p")]
		public IReadOnlyList<int> GamesOf5p { get; init; } = [];
		[JsonPropertyName("games_of_6p")]
		public IReadOnlyList<int> GamesOf6p { get; init; } = [];
		[JsonPropertyName("games_of_7p")]
		public IReadOnlyList<int> GamesOf7p { get; init; } = [];
		[JsonPropertyName("games_of_8p")]
		public IReadOnlyList<int> GamesOf8p { get; init; } = [];
		public IReadOnlyList<int> CustomGames { get; init; } = [];
		public IReadOnlyList<int> QuickMatches { get; init; } = [];
	}

	// GET /api/v1/players/:id/stats; client-private stats (locale, options, system spec, fps, ladder host) are never sent
	[ApiFixture("get-players-stats.response")]
	public sealed record PlayerStatsResponse
	{
		public long UserId { get; init; }
		public int EloRating { get; init; }
		public int MonthlyEloRating { get; init; }
		public int EloMatches { get; init; }
		public GeneralStats Generals { get; init; } = new GeneralStats();
		public int GamesAsRandom { get; init; }
		public int LastGeneral { get; init; }
		public int GamesInRowWithLastGeneral { get; init; }
		public int ChallengeMedals { get; init; }
		public int BattleHonors { get; init; }
		public int QmWinsInARow { get; init; }
		public int MaxQmWinsInARow { get; init; }
		public int WinsInARow { get; init; }
		public int MaxWinsInARow { get; init; }
		public int LossesInARow { get; init; }
		public int MaxLossesInARow { get; init; }
		public int DisconsInARow { get; init; }
		public int MaxDisconsInARow { get; init; }
		public int DesyncsInARow { get; init; }
		public int MaxDesyncsInARow { get; init; }
		public int BuiltParticleCannon { get; init; }
		public int BuiltNuke { get; init; }
		public int BuiltScud { get; init; }

		public static PlayerStatsResponse Of(PlayerStats stats)
		{
			return new PlayerStatsResponse
			{
				UserId = stats.userID,
				EloRating = stats.EloRating,
				MonthlyEloRating = stats.MonthlyEloRating,
				EloMatches = stats.EloMatches,
				Generals = new GeneralStats
				{
					Wins = stats.wins.ToArray(),
					Losses = stats.losses.ToArray(),
					Games = stats.games.ToArray(),
					Duration = stats.duration.ToArray(),
					UnitsKilled = stats.unitsKilled.ToArray(),
					UnitsLost = stats.unitsLost.ToArray(),
					UnitsBuilt = stats.unitsBuilt.ToArray(),
					BuildingsKilled = stats.buildingsKilled.ToArray(),
					BuildingsLost = stats.buildingsLost.ToArray(),
					BuildingsBuilt = stats.buildingsBuilt.ToArray(),
					Earnings = stats.earnings.ToArray(),
					TechCaptured = stats.techCaptured.ToArray(),
					Discons = stats.discons.ToArray(),
					Desyncs = stats.desyncs.ToArray(),
					Surrenders = stats.surrenders.ToArray(),
					GamesOf2p = stats.gamesOf2p.ToArray(),
					GamesOf3p = stats.gamesOf3p.ToArray(),
					GamesOf4p = stats.gamesOf4p.ToArray(),
					GamesOf5p = stats.gamesOf5p.ToArray(),
					GamesOf6p = stats.gamesOf6p.ToArray(),
					GamesOf7p = stats.gamesOf7p.ToArray(),
					GamesOf8p = stats.gamesOf8p.ToArray(),
					CustomGames = stats.customGamesPerGeneral.ToArray(),
					QuickMatches = stats.quickMatchesPerGeneral.ToArray()
				},
				GamesAsRandom = stats.gamesAsRandom,
				LastGeneral = stats.lastGeneral,
				GamesInRowWithLastGeneral = stats.gamesInRowWithLastGeneral,
				ChallengeMedals = stats.challengeMedals,
				BattleHonors = stats.battleHonors,
				QmWinsInARow = stats.QMwinsInARow,
				MaxQmWinsInARow = stats.maxQMwinsInARow,
				WinsInARow = stats.winsInARow,
				MaxWinsInARow = stats.maxWinsInARow,
				LossesInARow = stats.lossesInARow,
				MaxLossesInARow = stats.maxLossesInARow,
				DisconsInARow = stats.disconsInARow,
				MaxDisconsInARow = stats.maxDisconsInARow,
				DesyncsInARow = stats.desyncsInARow,
				MaxDesyncsInARow = stats.maxDesyncsInARow,
				BuiltParticleCannon = stats.builtParticleCannon,
				BuiltNuke = stats.builtNuke,
				BuiltScud = stats.builtSCUD
			};
		}
	}

	// GET /api/v1/players/stats?ids=: in the order of ids, unknown ids left out
	[ApiFixture("get-players-stats-ids.response")]
	public sealed record PlayerStatsBatchResponse
	{
		public IReadOnlyList<PlayerStatsResponse> Stats { get; init; } = [];
	}
}
