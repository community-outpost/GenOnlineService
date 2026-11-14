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

		// A result is accepted until this long after the match finished, or after it started while it has no end
		public static readonly TimeSpan ResultWindow = TimeSpan.FromHours(24);

		// Only a player of the match reports for it, the result is stored again on every call with a new body
		public static async Task<Result<MatchResultResponse>> SubmitResultAsync(IDbContextFactory<AppDbContext> dbFactory, LobbyManager lobbyManager, Int64 me, Int64 matchIDValue, MatchResultRequest request)
		{
			if (matchIDValue <= 0) return Result.Invalid("id");
			if (request.DurationS < 0) return Result.Invalid("duration_s");
			if (request.Stats == null) return Result.Invalid("stats");

			UInt64 matchID = (UInt64)matchIDValue;
			string body = JsonSerializer.Serialize(request, ApiResults.Json);
			(long, long) key = (matchIDValue, me);
			if (s_answers.TryGetValue(key, out var kept) && kept.Body == body && DateTime.UtcNow - kept.At < s_answerLifetime)
			{
				return Result.Success(kept.Answer);
			}

			// Eligibility comes from the stored roster, so a retry after the realtime connection closed still counts
			var seat = await FindSeatAsync(dbFactory, matchIDValue, me);
			if (seat == null)
			{
				return Result.NotFound("match");
			}
			if (DateTime.UtcNow - (seat.Value.FinishedAt ?? seat.Value.StartedAt) > ResultWindow)
			{
				return Result.Fail(ErrorCodes.MatchResultClosed);
			}
			int slotIndexInLobby = seat.Value.Slot;

			// The upload folder date follows the lobby creation time like the other uploads of the match, the match start when the lobby is gone
			Lobby? outcomeLobby = lobbyManager.GetLobbies().FirstOrDefault(lobby => lobby.MatchID == matchID);
			DateTime lobbyCreationTime = outcomeLobby?.TimeCreated ?? seat.Value.StartedAt;
			if (outcomeLobby == null)
			{
				foreach (UserSession session in WebSocketManager.GetAllDataFromUser(me))
				{
					if (session.WasPlayerInMatch(matchID, out _, out _, out DateTime createdAt))
					{
						lobbyCreationTime = createdAt;
						break;
					}
				}
			}

			// The lobby outlives a disconnected player's session, so the report is kept there for the winner decision
			outcomeLobby?.RecordPlayerOutcome(me, request.Won);

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

		// The caller's slot in the stored match with the match start and end, null when the caller did not play in it
		private static async Task<(int Slot, DateTime StartedAt, DateTime? FinishedAt)?> FindSeatAsync(IDbContextFactory<AppDbContext> dbFactory, Int64 matchID, Int64 me)
		{
			await using AppDbContext db = await dbFactory.CreateDbContextAsync();
			var seat = await db.MatchPlayers.AsNoTracking()
				.Where(player => player.MatchId == matchID && player.UserId == me)
				.Join(db.Matches.AsNoTracking(), player => player.MatchId, match => match.Id, (player, match) => new { player.Slot, match.StartedAt, match.FinishedAt })
				.FirstOrDefaultAsync();
			if (seat == null)
			{
				return null;
			}
			DateTime? finishedAt = seat.FinishedAt == null ? null : DateTime.SpecifyKind(seat.FinishedAt.Value, DateTimeKind.Utc);
			return (seat.Slot, DateTime.SpecifyKind(seat.StartedAt, DateTimeKind.Utc), finishedAt);
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
