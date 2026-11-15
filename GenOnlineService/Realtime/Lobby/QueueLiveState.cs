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

using GenOnlineService;
using GenOnlineService.Failover;
using GenOnlineService.Realtime;
using System.Collections.Concurrent;
using System.Diagnostics;

// The queue as live state: written after every matchmaking tick, restored as tickets that wait for their players to reconnect
static partial class MatchmakingManager
{
	private static string? s_livePublished;
	private static int s_liveGeneration = -1;
	private static long s_liveNextRewrite;

	// The tickets of a restored queue by user id, with the bucket they were in
	private static readonly ConcurrentDictionary<long, (QueueTicketLive Ticket, QueueBucketLive? Bucket)> s_restoredTickets = new();

	// The bucket that stands in for a restored one, so its members find each other again with the widening it had
	private static readonly ConcurrentDictionary<int, MatchmakingBucket> s_restoredBuckets = new();
	private static readonly object s_restoreLock = new();
	private static long s_restoredDeadline;

	internal static bool HasRestoredTickets => !s_restoredTickets.IsEmpty;

	internal const string PhaseSearching = "searching";
	internal const string PhaseCommitted = "committed";

	private static LiveBuild LiveBuildOf(GameBuild build) => new LiveBuild { Game = build.Game, ExeCrc = build.ExeCRC, IniCrc = build.IniCRC, AnticheatId = (int)build.AnticheatID };

	private static GameBuild GameBuildOf(LiveBuild build) => new GameBuild(build.Game ?? String.Empty, build.ExeCrc, build.IniCrc, (EKnownAnticheatID)build.AnticheatId);

	// The queue now: every registered session, in its bucket or still waiting to be sorted into one
	internal static QueueLive CaptureQueue()
	{
		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		List<QueueBucketLive> buckets = new();
		List<QueueTicketLive> tickets = new();
		HashSet<long> seen = new();

		QueueTicketLive TicketOf(UserSession session, int bucket)
		{
			long started = Interlocked.Read(ref session.MatchmakingStartedTimestamp);
			long waitedMs = started == 0 ? 0 : (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			return new QueueTicketLive
			{
				UserId = session.m_UserID,
				PlaylistId = session.MatchmakingPlaylistID,
				MapIndexes = session.MatchmakingMapIndicies.ToList(),
				Build = LiveBuildOf(session.MatchmakingBuild),
				QueuedAtMs = nowMs - waitedMs,
				Bucket = bucket
			};
		}

		foreach ((UInt16 _, ConcurrentBag<MatchmakingBucket> bag) in m_dictMatchmakingBuckets.OrderBy(entry => entry.Key))
		{
			foreach (MatchmakingBucket bucket in bag)
			{
				(QueueBucketLive Live, List<UserSession> Sessions)? captured = bucket.CaptureLive(buckets.Count);
				if (captured == null)
				{
					continue;
				}
				buckets.Add(captured.Value.Live);
				foreach (UserSession session in captured.Value.Sessions)
				{
					if (seen.Add(session.m_UserID))
					{
						tickets.Add(TicketOf(session, captured.Value.Live.Index));
					}
				}
			}
		}

		foreach (WeakReference<UserSession> reference in lstSessions.ToList())
		{
			if (reference.TryGetTarget(out UserSession? session) && session != null && !session.IsRetired && session.IsRegisteredForMatchmaking && seen.Add(session.m_UserID))
			{
				tickets.Add(TicketOf(session, -1));
			}
		}

		// restored tickets nobody reclaimed yet stay in the queue, so a second failover within the grace keeps them
		if (Environment.TickCount64 < Interlocked.Read(ref s_restoredDeadline))
		{
			Dictionary<int, int> restoredIndexes = new();
			foreach (KeyValuePair<long, (QueueTicketLive Ticket, QueueBucketLive? Bucket)> entry in s_restoredTickets.OrderBy(entry => entry.Key))
			{
				if (!seen.Add(entry.Key))
				{
					continue;
				}
				int index = -1;
				QueueBucketLive? bucket = entry.Value.Bucket;
				if (bucket != null && !restoredIndexes.TryGetValue(bucket.Index, out index))
				{
					index = buckets.Count;
					restoredIndexes[bucket.Index] = index;
					buckets.Add(bucket with { Index = index });
				}
				tickets.Add(entry.Value.Ticket with { Bucket = index });
			}
		}

		return new QueueLive { Tickets = tickets, Buckets = buckets };
	}

	// The tick seam: the queue is written when it differs from what was written last or every entity must be written again
	internal static void PublishLiveState()
	{
		if (!RealtimeLiveState.Writing)
		{
			return;
		}

		int generation = RealtimeLiveState.Generation;
		string json = LiveJson.Data(CaptureQueue());
		long now = Environment.TickCount64;
		if (now < s_liveNextRewrite && generation == s_liveGeneration && String.Equals(json, s_livePublished, StringComparison.Ordinal))
		{
			return;
		}
		s_liveNextRewrite = now + RealtimeLiveState.RewriteMs;
		s_livePublished = json;
		s_liveGeneration = generation;
		RealtimeLiveState.Set(LiveKeys.Queue, json);
	}

	// Keeps the restored tickets until their players reconnect or the grace ends, a committed start stays out since its lobby is restored in game
	internal static int RestoreQueue(QueueLive queue, long graceMs)
	{
		s_restoredTickets.Clear();
		s_restoredBuckets.Clear();
		Interlocked.Exchange(ref s_restoredDeadline, Environment.TickCount64 + graceMs);
		foreach (QueueTicketLive ticket in queue.Tickets ?? new())
		{
			QueueBucketLive? bucket = ticket.Bucket < 0 ? null : (queue.Buckets ?? new()).FirstOrDefault(b => b.Index == ticket.Bucket);
			if (bucket?.Phase == PhaseCommitted || ticket.PlaylistId < 0 || ticket.PlaylistId > UInt16.MaxValue || !g_Playlists.ContainsKey((UInt16)ticket.PlaylistId))
			{
				continue;
			}
			s_restoredTickets[ticket.UserId] = (ticket, bucket);
		}
		s_livePublished = null;
		return s_restoredTickets.Count;
	}

	// Tickets whose player did not come back within the grace are dropped
	internal static void DropExpiredRestoredTickets()
	{
		if (!s_restoredTickets.IsEmpty && Environment.TickCount64 >= Interlocked.Read(ref s_restoredDeadline))
		{
			int dropped = s_restoredTickets.Count;
			s_restoredTickets.Clear();
			s_restoredBuckets.Clear();
			s_log.LogInformation("Dropped {Count} restored matchmaking tickets whose players did not reconnect", dropped);
		}
	}

	// Queues a reconnecting player again with its playlist, maps, build and wait, a matched setup in progress also sends MatchRequeue setup_failed
	internal static async Task<bool> ReattachRestoredTicketAsync(UserSession session)
	{
		if (!s_restoredTickets.TryRemove(session.m_UserID, out (QueueTicketLive Ticket, QueueBucketLive? Bucket) restored)
			|| Environment.TickCount64 >= Interlocked.Read(ref s_restoredDeadline)
			|| !g_Playlists.TryGetValue((UInt16)restored.Ticket.PlaylistId, out Playlist? playlist)
			|| GenOnlineService.WebSocketManager.GetSharedDataForUser(session.m_UserID)?.GameStats == null)
		{
			return false;
		}

		List<int> maps = (restored.Ticket.MapIndexes ?? new()).Where(index => index >= 0 && index < playlist.Maps.Count).Distinct().ToList();
		if (maps.Count < MinSelectedMapsOf(playlist))
		{
			return false;
		}

		bool requeued = restored.Bucket != null && restored.Bucket.Phase != PhaseSearching;
		await session.MatchmakingStateLock.WaitAsync();
		try
		{
			if (session.IsRetired || session.IsRegisteredForMatchmaking)
			{
				return false;
			}

			long waitedMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - restored.Ticket.QueuedAtMs);
			session.MatchmakingPlaylistID = playlist.PlaylistID;
			session.MatchmakingMapIndicies = new ConcurrentList<int>(maps);
			session.MatchmakingBuild = GameBuildOf(restored.Ticket.Build ?? new LiveBuild());
			session.IsRegisteredForMatchmaking = true;
			session.MatchmakingStartedTimestamp = Stopwatch.GetTimestamp() - (long)(waitedMs * (Stopwatch.Frequency / 1000.0));

			if (restored.Bucket == null || requeued || !await JoinRestoredBucketAsync(session, playlist, restored.Bucket))
			{
				lstSessions.Add(new WeakReference<UserSession>(session));
			}
		}
		finally
		{
			session.MatchmakingStateLock.Release();
		}

		await SendStatus(session, StatusCodes.Queued, new { PlaylistId = (int)playlist.PlaylistID });
		if (requeued)
		{
			RealtimeOut.Fire(RealtimeOut.UserGame(session.m_UserID).MatchRequeue(new MatchRequeueEvent { Reason = MatchRequeueReason.SetupFailed }));
		}
		RealtimeHub.PublishPresence(session.m_UserID);
		return true;
	}

	// The first member back creates the stand in bucket with the stored widening, the others join it, caller holds the session's matchmaking lock
	private static async Task<bool> JoinRestoredBucketAsync(UserSession session, Playlist playlist, QueueBucketLive stored)
	{
		MatchmakingBucket? existing;
		lock (s_restoreLock)
		{
			if (!s_restoredBuckets.TryGetValue(stored.Index, out existing) || existing.IsMergedAway() || existing.CurrentMemberCount() == 0)
			{
				MatchmakingBucket created = new MatchmakingBucket(playlist.PlaylistID, session, playlist.MinPlayers, playlist.DesiredPlayers,
					new ConcurrentList<int>(stored.MapIndexes ?? new()), GameBuildOf(stored.Build ?? new LiveBuild()));
				created.RestoreWidening(stored.EloExpansionIteration);
				m_dictMatchmakingBuckets.GetOrAdd(playlist.PlaylistID, _ => new ConcurrentBag<MatchmakingBucket>()).Add(created);
				s_restoredBuckets[stored.Index] = created;
				existing = null;
			}
		}

		if (existing == null)
		{
			await SendSearching(session, 1, playlist.DesiredPlayers, playlist.MinPlayers);
			return true;
		}
		return await existing.JoinAsync(session);
	}

	public partial class MatchmakingBucket
	{
		// The bucket under its own lock: phase, widening, lobby and live members, null when it no longer takes part
		internal (QueueBucketLive Live, List<UserSession> Sessions)? CaptureLive(int index)
		{
			lock (m_StateLock)
			{
				if (m_bMergedAway || m_bPendingDeletion)
				{
					return null;
				}

				List<UserSession> sessions = m_lstMembers.ToList()
					.Select(member => member.GetAssociatedSession())
					.Where(session => session != null && !session.IsRetired && session.IsRegisteredForMatchmaking)
					.Cast<UserSession>()
					.ToList();
				string phase = m_bStartCommitted ? PhaseCommitted
					: m_bHasStartedCountdown ? "countdown"
					: m_bWaitingOnMeshConnectivityChecks ? "mesh_check"
					: m_bWaitingOnLobbyJoins ? "joining"
					: m_bCreatingLobby ? "creating"
					: PhaseSearching;
				return (new QueueBucketLive
				{
					Index = index,
					PlaylistId = PlaylistID,
					MapIndexes = lstMapIndices.ToList(),
					Build = LiveBuildOf(Build),
					EloExpansionIteration = eloExpansionIteration,
					Phase = phase,
					LobbyId = m_LobbyID,
					Members = sessions.Select(session => session.m_UserID).ToList()
				}, sessions);
			}
		}

		// A bucket that stands in for one of the earlier copy keeps its widening, the next widening step counts from now
		internal void RestoreWidening(int iteration)
		{
			eloExpansionIteration = Math.Max(1, iteration);
			m_LastELOExpansionTime = DateTime.Now;
		}
	}
}
