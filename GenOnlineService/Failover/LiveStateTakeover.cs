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

namespace GenOnlineService.Failover
{
	public sealed record LiveRestoreResult(int Restored, int Lost, int Skipped, int Tickets);

	// What a copy does with the mirror when it becomes the owner, before it reports ready
	public static class LiveStateTakeover
	{
		private static readonly ILogger s_log = AppLog.For(typeof(LiveStateTakeover));

		// Only a takeover after a failure restores the mirror, every activation then writes the state again under the new epoch
		public static void OnActivating(FailoverActivation activation, long epoch, LobbyManager lobbies)
		{
			(IReadOnlyDictionary<string, string> mirror, bool fresh) = RealtimeLiveState.DetachMirror();
			if (activation == FailoverActivation.AfterExpiry && !fresh)
			{
				// a mirror not in sync lately may hold lobbies minutes old, the copy starts empty as a cold start does
				AppMetrics.RecordFailoverLiveState("restore_skipped_stale");
				s_log.LogWarning("Copy took over with epoch {Epoch} but its mirror was not in sync lately, {Count} stored entities are not restored", epoch, mirror.Count);
			}
			else if (activation == FailoverActivation.AfterExpiry)
			{
				LiveRestoreResult result = Restore(mirror, lobbies, UserSessionSettings.ReconnectGracePeriodMS);
				s_log.LogInformation("Copy took over with epoch {Epoch}: {Restored} lobbies restored, {Lost} lost, {Skipped} skipped, {Tickets} matchmaking tickets waiting for their players",
					epoch, result.Restored, result.Lost, result.Skipped, result.Tickets);
			}
			RealtimeLiveState.Activated();
		}

		// Each entity on its own: an unreadable one is skipped and counted, never a crash
		public static LiveRestoreResult Restore(IReadOnlyDictionary<string, string> mirror, LobbyManager lobbies, long graceMs)
		{
			int restored = 0, lost = 0, skipped = 0, tickets = 0;
			List<(Lobby Lobby, long WrittenAtMs)> candidates = new();
			foreach ((string key, string json) in mirror)
			{
				if (!LiveKeys.IsLobby(key))
				{
					continue;
				}

				try
				{
					LiveEnvelope? envelope = LiveJson.ReadEnvelope(json);
					LobbyLive? live = envelope == null ? null : LiveJson.ReadData<LobbyLive>(envelope);
					if (live == null)
					{
						throw new FormatException("unreadable envelope or data");
					}

					string? skip = Lobby.SkipReasonOf(live);
					if (skip != null)
					{
						++skipped;
						s_log.LogInformation("Restored lobby {LobbyId} is not taken over: {Reason}", live.Id, skip);
						continue;
					}

					candidates.Add((Lobby.FromLive(live), envelope!.WrittenAtMs));
				}
				catch (Exception ex)
				{
					++lost;
					s_log.LogWarning(ex, "The live state {Key} could not be restored", key);
				}
			}

			// a user seated in two stored lobbies keeps the seat of the newer one
			Dictionary<long, long> seats = new();
			foreach ((Lobby lobby, long _) in candidates.OrderByDescending(candidate => candidate.WrittenAtMs).ThenByDescending(candidate => candidate.Lobby.LobbyID))
			{
				foreach (long userID in lobby.MembersWithoutSession())
				{
					if (seats.TryGetValue(userID, out long newer))
					{
						s_log.LogWarning("User {UserId} is stored in lobby {LobbyId} and in the newer lobby {NewerLobbyId}, the seat in the newer one is kept", userID, lobby.LobbyID, newer);
						lobby.RemoveRestoredSeat(userID);
					}
					else
					{
						seats[userID] = lobby.LobbyID;
					}
				}

				if (lobby.GetNumberOfHumans() == 0 || !lobbies.AdoptRestoredLobby(lobby, graceMs))
				{
					++skipped;
				}
				else
				{
					++restored;
				}
			}

			if (mirror.TryGetValue(LiveKeys.Queue, out string? queueJson))
			{
				LiveEnvelope? envelope = LiveJson.ReadEnvelope(queueJson);
				QueueLive? queue = envelope == null ? null : LiveJson.ReadData<QueueLive>(envelope);
				if (queue == null)
				{
					AppMetrics.RecordFailoverLiveState("queue_lost");
					s_log.LogWarning("The live state of the matchmaking queue could not be restored");
				}
				else
				{
					tickets = MatchmakingManager.RestoreQueue(queue, graceMs);
				}
			}

			AppMetrics.RecordFailoverLobbiesRestored(restored, lost);
			return new LiveRestoreResult(restored, lost, skipped, tickets);
		}
	}
}
