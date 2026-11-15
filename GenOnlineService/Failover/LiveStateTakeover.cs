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

		// The stored state a copy restores and claims when it becomes the holder, before it reports ready.
		// After a failure the fresh mirror, after a cancelled handover what the target owned, nothing on a cold start.
		// Every entity another copy owned is claimed at its owner epoch plus one, and what was not restored is deleted.
		public static void OnActivating(FailoverActivation activation, long epoch, LobbyManager lobbies, LiveStateWriter writer, IReadOnlyDictionary<string, string>? takenBack, string? target)
		{
			(IReadOnlyDictionary<string, string> mirror, bool fresh) = RealtimeLiveState.DetachMirror();
			IReadOnlyDictionary<string, string> source = activation switch
			{
				FailoverActivation.TakenBack => takenBack ?? new Dictionary<string, string>(),
				FailoverActivation.HandedOver => mirror,
				FailoverActivation.AfterExpiry when fresh => mirror,
				_ => new Dictionary<string, string>()
			};
			if (activation == FailoverActivation.AfterExpiry && !fresh)
			{
				// a mirror not in sync lately may hold lobbies minutes old, the copy starts empty as a cold start does
				AppMetrics.RecordFailoverLiveState("restore_skipped_stale");
				s_log.LogWarning("Copy took over with epoch {Epoch} but its mirror was not in sync lately, {Count} stored entities are not restored", epoch, mirror.Count);
			}

			// entities this copy owns are live here, one transferred to it but not adopted yet is restored too
			Dictionary<string, string> foreign = source
				.Where(entry => !writer.Owns(entry.Key))
				.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
			HashSet<string> restoredKeys = new(StringComparer.Ordinal);
			LiveRestoreResult result = Restore(foreign, lobbies, UserSessionSettings.ReconnectGracePeriodMS, restoredKeys);
			if (activation is FailoverActivation.AfterExpiry or FailoverActivation.TakenBack || result.Restored + result.Lost > 0)
			{
				s_log.LogInformation("Copy took over with epoch {Epoch}: {Restored} lobbies restored, {Lost} lost, {Skipped} skipped, {Tickets} matchmaking tickets waiting for their players",
					epoch, result.Restored, result.Lost, result.Skipped, result.Tickets);
			}

			MatchmakingManager.HoldFormation = false;
			Task<IReadOnlyDictionary<string, long>> claim = activation == FailoverActivation.TakenBack
				? writer.ClaimAsync(LiveClaim.OwnedBy, target, new[] { LiveKeys.Queue })
				: writer.ClaimAsync(LiveClaim.NotMine, null, new[] { LiveKeys.Queue });
			_ = claim.ContinueWith(done =>
			{
				if (!done.IsCompletedSuccessfully)
				{
					return;
				}
				foreach (string key in done.Result.Keys)
				{
					if (LiveKeys.IsLobby(key) && !restoredKeys.Contains(key) && !(Int64.TryParse(key.AsSpan(LiveKeys.LobbyPrefix.Length), out long id) && lobbies.GetLobby(id) != null))
					{
						writer.Delete(key);
					}

					// a leave note or a ticket the other copy never applied: the leave applies here, the note goes
					else if (LiveKeys.IsLeave(key) || LiveKeys.IsTicket(key))
					{
						writer.Delete(key);
					}
				}
				MatchmakingManager.ForceLivePublish();
			}, TaskScheduler.Default);
		}

		// Each entity on its own: an unreadable one is skipped and counted, never a crash
		public static LiveRestoreResult Restore(IReadOnlyDictionary<string, string> mirror, LobbyManager lobbies, long graceMs, ISet<string>? restoredKeys = null)
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
					restoredKeys?.Add(LiveKeys.Lobby(lobby.LobbyID));
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
					restoredKeys?.Add(LiveKeys.Queue);
				}
			}

			AppMetrics.RecordFailoverLobbiesRestored(restored, lost);
			return new LiveRestoreResult(restored, lost, skipped, tickets);
		}
	}
}
