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
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GenOnlineService.Realtime
{
	// Match results and playlists (section 10.3)
	public static class MatchActions
	{
		// Answers kept so a repeated result with the same body gets the same upload targets
		private static readonly TimeSpan s_answerLifetime = TimeSpan.FromHours(1);
		private static readonly ConcurrentDictionary<(long MatchID, long UserID), (string Body, MatchResultResponse Answer, DateTime At)> s_answers = new();

		// Only a player of the match reports for it, the result is stored again on every call with a new body
		public static async Task<Result<MatchResultResponse>> SubmitResultAsync(IDbContextFactory<AppDbContext> dbFactory, LobbyManager lobbyManager, Int64 me, Int64 matchIDValue, MatchResultRequest request)
		{
			if (matchIDValue <= 0) return Result.Invalid("id");
			if (request.DurationS < 0) return Result.Invalid("duration_s");
			if (request.Stats == null) return Result.Invalid("stats");

			UInt64 matchID = (UInt64)matchIDValue;
			int slotIndexInLobby = -1;
			DateTime lobbyCreationTime = DateTime.UnixEpoch;
			UserSession? session = WebSocketManager.GetAllDataFromUser(me).FirstOrDefault(s => s.WasPlayerInMatch(matchID, out slotIndexInLobby, out _, out lobbyCreationTime));
			if (session == null)
			{
				return Result.NotFound("match");
			}

			string body = JsonSerializer.Serialize(request, ApiResults.Json);
			(long, long) key = (matchIDValue, me);
			if (s_answers.TryGetValue(key, out var kept) && kept.Body == body && DateTime.UtcNow - kept.At < s_answerLifetime)
			{
				return Result.Success(kept.Answer);
			}

			// The lobby outlives a disconnected player's session, so the report is kept there for the winner decision.
			// The match id check keeps a stale report away from whatever lobby the player is in now.
			Lobby? outcomeLobby = lobbyManager.GetLobby(session.currentLobbyID);
			if (outcomeLobby != null && outcomeLobby.MatchID == matchID)
			{
				outcomeLobby.RecordPlayerOutcome(me, request.Won);
			}

			string screenshotURL = await S3CredentialManager.GetPresignedURL(EMetadataFileType.FILE_TYPE_SCREENSHOT, EScreenshotType.SCREENSHOT_TYPE_SCORESCREEN, matchID, me, slotIndexInLobby, lobbyCreationTime) ?? String.Empty;
			string replayURL = await S3CredentialManager.GetPresignedURL(EMetadataFileType.FILE_TYPE_REPLAY, EScreenshotType.NONE, matchID, me, slotIndexInLobby, lobbyCreationTime) ?? String.Empty;

			MatchStats stats = request.Stats;
			await using (AppDbContext db = await dbFactory.CreateDbContextAsync())
			{
				await global::Database.MatchHistory.CommitPlayerOutcome(db, slotIndexInLobby, matchID, request.Side,
					stats.BuildingsBuilt, stats.BuildingsKilled, stats.BuildingsLost, stats.UnitsBuilt, stats.UnitsKilled, stats.UnitsLost,
					(int)Math.Clamp(stats.TotalMoney, Int32.MinValue, Int32.MaxValue), request.Won, request.Desynced, (UInt32)request.DurationS);
			}

			MatchResultResponse answer = new MatchResultResponse { ScreenshotUrl = screenshotURL, ReplayUrl = replayURL };
			DateTime now = DateTime.UtcNow;
			foreach (KeyValuePair<(long MatchID, long UserID), (string Body, MatchResultResponse Answer, DateTime At)> old in s_answers)
			{
				if (now - old.Value.At >= s_answerLifetime)
				{
					s_answers.TryRemove(old);
				}
			}
			s_answers[key] = (body, answer, now);
			return Result.Success(answer);
		}

		public static PlaylistsResponse Playlists()
		{
			Playlist[] playlists = MatchmakingManager.GetPlaylists().Values.OrderBy(playlist => playlist.PlaylistID).Select(playlist => new Playlist
			{
				Id = playlist.PlaylistID,
				Name = playlist.Name,
				MinPlayers = playlist.MinPlayers,
				DesiredPlayers = playlist.DesiredPlayers,
				MinSelectedMaps = MatchmakingManager.MinSelectedMapsOf(playlist),
				AllowTeams = playlist.AllowTeams,
				TeamSize = playlist.TeamSize < 0 ? null : playlist.TeamSize,
				AllowArmySelection = playlist.AllowArmySelection,
				GracePeriodS = (playlist.GracePeriodAtMinPlayersMSec + 999) / 1000,
				Maps = playlist.Maps.Select((map, index) => new PlaylistMap
				{
					Index = index,
					Name = map.Name,
					Path = map.Path,
					Custom = map.Custom,
					MaxPlayers = map.MaxPlayers
				}).ToArray()
			}).ToArray();

			return new PlaylistsResponse { Playlists = playlists };
		}
	}
}
