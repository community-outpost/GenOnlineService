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

using Database;
using Discord.Rest;
using GenOnlineService;
using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

public class PlaylistMap
{
	public string Name { get; private set; }
	public string Path { get; private set; }
	public bool Custom { get; private set; }
	public int MaxPlayers { get; private set; }

	public PlaylistMap(string strName, string strPath, bool bCustom, int maxPlayers)
	{
		Name = strName;
		Path = strPath;
		Custom = bCustom;
		MaxPlayers = maxPlayers;
	}
}

public class ConcurrentList<T>
{
	private List<T> m_internalList;
	private readonly object m_lockObj = new object();

	public ConcurrentList()
	{
		m_internalList = new List<T>();
	}

	public ConcurrentList(IEnumerable<T> collection)
	{
		if (collection != null)
		{
			m_internalList = new List<T>(collection);
		}
		else
		{
			m_internalList = new List<T>();
		}
	}

	public void AddRange(IEnumerable<T> collection)
	{
		lock (m_lockObj)
		{
			m_internalList.AddRange(collection);
		}
	}

	public void Add(T item)
	{
		lock (m_lockObj)
		{
			m_internalList.Add(item);
		}
	}

	public void Clear()
	{
		lock (m_lockObj)
		{
			m_internalList.Clear();
		}
	}

	public T this[int index]
	{
		get
		{
			lock (m_lockObj)
			{
				if (index < 0 || index >= m_internalList.Count)
					throw new IndexOutOfRangeException("Index out of range.");
				return m_internalList[index];
			}
		}
	}

	public int Count
	{
		get
		{
			lock (m_lockObj)
			{
				return m_internalList.Count;
			}
		}
	}

	public bool Contains(T item)
	{
		lock (m_lockObj)
		{
			return m_internalList.Contains(item);
		}
	}
	public bool Remove(T item)
	{
		lock (m_lockObj)
		{
			return m_internalList.Remove(item);
		}
	}
	public List<T> ToList()
	{
		lock (m_lockObj)
		{
			return m_internalList.ToList();
		}
	}
	public IEnumerator<T> GetEnumerator()
	{
		lock (m_lockObj)
		{
			return m_internalList.ToList().GetEnumerator();
		}
	}
}

public class Playlist
{
	public UInt16 PlaylistID { get; private set; }
	public string Name { get; private set; }
	public int MinPlayers { get; private set; }
	public int DesiredPlayers { get; private set; }
	public int MinSelectedMaps { get; private set; }

	public bool AllowTeams { get; private set; }
	public int TeamSize { get; private set; }
	public bool AllowArmySelection { get; private set; }
	public UInt16 GracePeriodAtMinPlayersMSec { get; private set; }
	public List<PlaylistMap> Maps { get; private set; }

	public Playlist(UInt16 a_PlaylistID, string a_strName,
		int a_MinPlayers, int a_DesiredPlayers, int a_MinSelectedMaps, bool a_bAllowTeams, int a_TeamSize, bool a_bAllowArmySelection, UInt16 a_gracePeriodAtMinPlayersMSec, List<PlaylistMap> allowedMaps)
	{
		PlaylistID = a_PlaylistID;
		Name = a_strName;
		MinPlayers = a_MinPlayers;
		MinSelectedMaps = a_MinSelectedMaps;
		DesiredPlayers = a_DesiredPlayers;
		AllowTeams = a_bAllowTeams;
		TeamSize = a_TeamSize;
		AllowArmySelection = a_bAllowArmySelection;
		GracePeriodAtMinPlayersMSec = a_gracePeriodAtMinPlayersMSec;
		Maps = allowedMaps;
	}
}

static partial class MatchmakingManager
{
	private static readonly ILogger s_log = AppLog.For(typeof(MatchmakingManager));
	// World Series 2026 Qualification in September requires the matchmaking to be
	// based off the monthly ELO.
	internal static int GetMatchmakingElo(PlayerStats stats)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		DateTimeOffset septemberStart = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
		DateTimeOffset octoberStart = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

		return now >= septemberStart && now < octoberStart
			? stats.MonthlyEloRating
			: stats.EloRating;
	}

	// selects every map of the player's playlist, false when the player is not queued
	public static bool PlayerWidenSearch(UserSession playerSession)
	{
		if (!playerSession.IsRegisteredForMatchmaking)
		{
			return false;
		}

		// NOTE: we dont check the state of the bucket here, but it doesn't really matter since expanding the maps after it started won't do anything anyway

		// remove the map limitation
		foreach (var kvPair in m_dictMatchmakingBuckets)
		{
			foreach (MatchmakingBucket mmBucket in kvPair.Value)
			{
				if (mmBucket.HasPlayer(playerSession))
				{
					// get the maximum map set for this playlist
					if (MatchmakingManager.g_Playlists.TryGetValue(mmBucket.PlaylistID, out Playlist? playlist))
					{
						List<int> lstAllMaps = new();
						for (int mapIndex = 0; mapIndex < playlist.Maps.Count; ++mapIndex)
						{
							lstAllMaps.Add(mapIndex);
						}

						// are we the owner of the bucket? update the original list too

						MatchmakingBucketMember? bucketOwner = mmBucket.GetOwner();
						if (bucketOwner != null)
						{
							if (mmBucket.lstMapIndices.Count > 0 && bucketOwner.GetAssociatedSession() == playerSession)
							{
								mmBucket.lstMapIndices = new ConcurrentList<int>(lstAllMaps);
							}
						}

						// update our player too
						playerSession.MatchmakingMapIndicies = new ConcurrentList<int>(lstAllMaps);

						// can't be in multiple buckets
						return true;
					}
				}
			}
		}

		// not in a bucket yet (still queued waiting to be sorted) - widen their selection anyway, otherwise the
		// widen request is silently dropped and they get bucketed with their original map list
		if (MatchmakingManager.g_Playlists.TryGetValue(playerSession.MatchmakingPlaylistID, out Playlist? queuedPlaylist))
		{
			List<int> lstAllMaps = new();
			for (int mapIndex = 0; mapIndex < queuedPlaylist.Maps.Count; ++mapIndex)
			{
				lstAllMaps.Add(mapIndex);
			}

			playerSession.MatchmakingMapIndicies = new ConcurrentList<int>(lstAllMaps);
		}
		return true;
	}

	// MatchmakingStatus codes of section 6.4
	internal static class StatusCodes
	{
		public const string Queued = "matchmaking.queued";
		public const string Searching = "matchmaking.searching";
		public const string Expanding = "matchmaking.expanding";
		public const string GraceCountdown = "matchmaking.grace_countdown";
		public const string GraceCancelled = "matchmaking.grace_cancelled";
		public const string Creating = "matchmaking.creating";
		public const string Preparing = "matchmaking.preparing";
		public const string Starting = "matchmaking.starting";

		// the service took the player out of the queue, params carry the reason
		public const string Left = "matchmaking.left";
	}

	// Status goes to the player's game connection, nothing when it is disconnected
	private static Task SendStatus(UserSession session, string code, object? parameters = null)
	{
		RealtimeOut.Fire(RealtimeOut.UserGame(session.m_UserID).MatchmakingStatus(new MatchmakingStatusEvent { Code = code, Params = parameters ?? Empty.Instance }));
		return Task.CompletedTask;
	}

	private static Task SendSearching(UserSession session, int players, int desired, int required) =>
		SendStatus(session, StatusCodes.Searching, new { Players = players, Desired = desired, Required = required });

	// a player the service removed from matchmaking without a requeue
	private static Task SendLeft(UserSession session, string reason)
	{
		RealtimeHub.PublishPresence(session.m_UserID);
		return SendStatus(session, StatusCodes.Left, new { Reason = reason });
	}

	public class MatchmakingBucketMember
	{
		WeakReference<UserSession>? m_SessionRef = null;

		public MatchmakingBucketMember(UserSession owningSession)
		{
			m_SessionRef = new WeakReference<UserSession>(owningSession);
		}

		public bool Is(UserSession playerSession)
		{
			if (m_SessionRef != null)
			{
				if (m_SessionRef.TryGetTarget(out UserSession? thisSession))
				{
					if (thisSession != null && thisSession == playerSession)
					{
						return true;
					}
				}
			}

			return false;
		}

		public UserSession? GetAssociatedSession()
		{
			if (m_SessionRef != null)
			{
				if (m_SessionRef.TryGetTarget(out UserSession? thisSession))
				{
					return thisSession;
				}
			}

			return null;
		}
	}

	public partial class MatchmakingBucket
	{
		private static readonly ILogger s_log = AppLog.For(typeof(MatchmakingBucket));
		private ConcurrentList<MatchmakingBucketMember> m_lstMembers = new();
		public ConcurrentList<int> lstMapIndices { get; set; } = new();

		private Int64 m_timeReachedMinPlayers = -1;
		private bool m_bReachedMinPlayers = false;
		private bool m_bWaitingOnLobbyJoins = false;
		private bool m_bHasStartedCountdown = false;
		private bool m_bWaitingOnMeshConnectivityChecks = false;
		private bool m_bAutoStartInvalidated = false;
		private bool m_bPendingDeletion = false;
		private bool m_bMergedAway = false;
		private bool m_bAbortInProgress = false;
		private bool m_bStartCommitted = false;
		private readonly object m_StateLock = new();

		// a bucket that has been merged into another bucket (or that has already been handed a lobby) must never
		// accept or donate members again, otherwise a player ends up in two buckets and gets sent to two lobbies
		public bool IsMergedAway()
		{
			lock (m_StateLock)
			{
				return m_bMergedAway;
			}
		}

		public bool IsLockedForMatchStart()
		{
			lock (m_StateLock)
			{
				return m_bCreatingLobby || m_bHasStartedCountdown || m_bWaitingOnLobbyJoins || m_bWaitingOnMeshConnectivityChecks || m_bPendingDeletion;
			}
		}

		private bool IsPendingDeletion()
		{
			lock (m_StateLock)
			{
				return m_bPendingDeletion;
			}
		}

		public UInt16 PlaylistID { get; private set; }
		public int MinPlayers { get; private set; }
		public int DesiredPlayers { get; private set; }

		// Only players of this exact build join the bucket, so two builds never form a match
		public GameBuild Build { get; private set; }

		public int eloExpansionIteration { get; private set; } = 1; // 1 * EloExpansionValue, matches the initial value

        public void ExpandElo()
		{
			++eloExpansionIteration;
			m_LastELOExpansionTime = DateTime.Now;
        }

		DateTime m_CreationTime = DateTime.Now;
        DateTime m_LastELOExpansionTime = DateTime.Now;

		private TimeSpan TimeSinceLastEloExpansion()
		{
            TimeSpan timeDifference = DateTime.Now - m_LastELOExpansionTime;
			return timeDifference;
        }

        public MatchmakingBucketMember? GetOwner()
		{
			if (m_lstMembers.Count > 0)
			{
				return m_lstMembers[0];
			}

			return null;
		}

		private sealed record Formation(ImmutableArray<UserSession> Players, PlaylistMap Map, bool UsedFallbackMap);
		private Formation? m_Formation;
		private bool m_bCreatingLobby;
		private static long s_NextBucketID;
		private readonly long m_BucketID = Interlocked.Increment(ref s_NextBucketID);

		private Formation? TryBeginFormation(Playlist playlist, bool graceExpired)
		{
			// only the copy that owns the queue forms matches, build step 4 moves it during a handover
			if (!FormsMatches) return null;
			lock (m_StateLock)
			{
				if (IsLockedForMatchStart() || m_bMergedAway) return null;
				var players = m_lstMembers.ToList().Select(member => member.GetAssociatedSession()).ToArray();
				if (players.Length < MinPlayers || players.Length > DesiredPlayers
					|| (!graceExpired && players.Length < DesiredPlayers)
					|| players.Any(player => player == null || player.IsRetired || !player.IsRegisteredForMatchmaking)) return null;

				var selectedPlayers = players.Cast<UserSession>().ToArray();
				var sharedMaps = new HashSet<int>(lstMapIndices.ToList());
				foreach (var player in selectedPlayers)
					sharedMaps.IntersectWith(player.MatchmakingMapIndicies.ToList());
				var exactMaps = playlist.Maps.Where(map => map.MaxPlayers == selectedPlayers.Length).ToArray();
				var preferredMaps = sharedMaps.Where(index => index >= 0 && index < playlist.Maps.Count)
					.Select(index => playlist.Maps[index]).Where(map => map.MaxPlayers == selectedPlayers.Length).ToArray();
				var choices = preferredMaps.Length > 0 ? preferredMaps : exactMaps;
				if (choices.Length == 0) return null;

				m_Formation = new Formation(selectedPlayers.ToImmutableArray(), choices[Random.Shared.Next(choices.Length)], preferredMaps.Length == 0);
				m_bCreatingLobby = true;
				m_bReachedMinPlayers = false;
				m_timeReachedMinPlayers = -1;
				return m_Formation;
			}
		}

		private bool IsFormationCurrent(Lobby? lobby = null)
		{
			lock (m_StateLock)
			{
				return m_Formation != null && !m_bAutoStartInvalidated && !m_bPendingDeletion
					&& CurrentMemberCount() == m_Formation.Players.Length
					&& m_Formation.Players.All(player => !player.IsRetired && player.IsRegisteredForMatchmaking && HasPlayer(player))
					&& (lobby == null || (lobby.NumCurrentPlayers == m_Formation.Players.Length
						&& m_Formation.Players.All(player => lobby.GetMemberFromUserID(player.m_UserID)?.GetSession()
							.TryGetTarget(out var memberSession) == true && ReferenceEquals(memberSession, player))));
			}
		}

		private async Task CreateFormationLobbyAsync(Playlist playlist, Formation formation)
		{
			var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
			try
			{
				using var scope = ServiceLocator.Services.CreateScope();
				var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using var db = await factory.CreateDbContextAsync();

				// the stored blocks decide before the match forms, so a block another copy made and this one missed never pairs the two
				var blocks = await RealtimeBlocks.BlocksAmongAsync(db, formation.Players.Select(player => player.m_UserID).ToArray());
				if (blocks is { Count: > 0 })
				{
					foreach (long blocker in blocks.Select(block => block.UserId).Distinct())
						await RealtimeBlocks.ReloadAsync(blocker);
					s_log.LogInformation("QuickMatch formation of bucket {BucketId} dropped for {Count} stored blocks among its players", m_BucketID, blocks.Count);
					await AbortQuickMatchAutoStartAsync(MatchRequeueReason.SetupFailed);
					return;
				}

				foreach (var player in formation.Players)
				{
					await Database.Leaderboards.CreateUserEntriesIfNotExists(db, player.m_UserID);
					await SendStatus(player, StatusCodes.Creating, new { Players = formation.Players.Length, Map = formation.Map.Name });
				}
				var host = formation.Players[0];
				var hostData = GenOnlineService.WebSocketManager.GetSharedDataForUser(host.m_UserID);
				if (hostData == null) throw new InvalidOperationException("QuickMatch host session disappeared.");
				if (!IsFormationCurrent()) throw new InvalidOperationException("QuickMatch formation changed.");
				m_LobbyID = await lobbyManager.CreateLobby(db, host, hostData.m_strDisplayName, "Quickmatch Lobby", formation.Map.Name, formation.Map.Path + ".map",
					true, formation.Players.Length, 12345, false, true, 10000, false, false, String.Empty, -5, false, Constants.g_DefaultCameraMaxHeight,
					Build, ELobbyType.QuickMatch);
				var lobby = lobbyManager.GetLobby(m_LobbyID) ?? throw new InvalidOperationException("QuickMatch lobby creation failed.");
				foreach (var player in formation.Players) lobby.MatchedUserIDs.Add(player.m_UserID);
				var found = new MatchFoundEvent
				{
					LobbyId = m_LobbyID,
					Map = new MapRef { Name = lobby.MapName, Path = lobby.MapPathWire, Official = lobby.IsMapOfficial },
					Players = formation.Players.Length
				};
				lock (m_StateLock)
				{
					if (!IsFormationCurrent()) throw new InvalidOperationException("QuickMatch formation changed.");
					m_bWaitingOnLobbyJoins = true;
					m_timeStartedWaitingOnLobbyJoins = Environment.TickCount64;
					foreach (var player in formation.Players) RealtimeOut.Fire(RealtimeOut.UserGame(player.m_UserID).MatchFound(found));
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "QuickMatch formation failed for bucket {BucketId}", m_BucketID);
				await AbortQuickMatchAutoStartAsync(MatchRequeueReason.SetupFailed);
			}
			finally
			{
				lock (m_StateLock) m_bCreatingLobby = false;
			}
		}

		public bool DoMapSelectionsIntersect(ConcurrentList<int> lstRhs)
		{
			var mapSet = new HashSet<int>(lstMapIndices.ToList());
			var mapSetRhs = new HashSet<int>(lstRhs.ToList());
			foreach (int mapIndex in mapSetRhs)
			{
				if (mapSet.Contains(mapIndex))
				{
					return true;
				}
			}
			return false;
		}

		public bool CanMergeWithOtherBucket(MatchmakingBucket bucketToMerge)
		{
			if (IsPendingDeletion() || bucketToMerge.IsPendingDeletion())
			{
				return false;
			}

			// playlist must match
			if (bucketToMerge.PlaylistID != this.PlaylistID)
			{
				return false;
			}

			// either bucket already merged away this tick? it is stale, never touch it again
			if (IsMergedAway() || bucketToMerge.IsMergedAway())
			{
				return false;
			}

			// if we're already counting down, dont let people join, just pretend we are full
			if (IsLockedForMatchStart())
			{
				return false;
			}

			// the other bucket may have already been given a lobby this tick - its members are on their way into
			// that lobby, so pulling them in here would matchmake them into a second lobby
			if (bucketToMerge.IsLockedForMatchStart())
			{
				return false;
			}

			// must have space for all users in the rhs bucket, and CRCs must match
			if (!HasSpaceForUsers(bucketToMerge.CurrentMemberCount(), bucketToMerge.Build))
			{
				return false;
			}

			// must be within the eloThreshold
			int eloExpansionToUse = (bucketToMerge.GetAvgElo() >= EloConfig.HighEloThreshold || GetAvgElo() >= EloConfig.HighEloThreshold) ? EloConfig.EloExpansionValue_HighELO : EloConfig.EloExpansionValue_Standard;
			if (!IsAvgEloWithinThreshold(bucketToMerge.GetAvgElo(), eloExpansionIteration * eloExpansionToUse))
			{
				return false;
			}

			// cant be blocked by any participant (or have any participant blocked)
			// TODO_OPTIMIZE: This is O(n^2)
			foreach (MatchmakingBucketMember rhsMember in bucketToMerge.m_lstMembers)
			{
				UserSession? rhsSession = rhsMember.GetAssociatedSession();

				if (rhsSession != null)
				{
                    if (IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(rhsSession, rhsSession.m_UserID))
					{
						return false;
                    }
                }
            }
                

			// must have overlap in our map selections
			if (!bucketToMerge.DoMapSelectionsIntersect(this.lstMapIndices))
			{
				return false;
			}

			return true;
		}

		public async Task<bool> MergeWithOtherBucketAsync(MatchmakingBucket bucketToMerge)
		{
			if (ReferenceEquals(this, bucketToMerge)) return false;
			var first = m_BucketID < bucketToMerge.m_BucketID ? this : bucketToMerge;
			var second = ReferenceEquals(first, this) ? bucketToMerge : this;
			lock (first.m_StateLock)
			lock (second.m_StateLock)
			{
				if (!CanMergeWithOtherBucket(bucketToMerge)) return false;
				foreach (var member in bucketToMerge.m_lstMembers)
				{
					var session = member.GetAssociatedSession();
					if (session != null && !session.IsRetired && !HasPlayer(session)) m_lstMembers.Add(member);
				}
				bucketToMerge.m_bMergedAway = true;
				bucketToMerge.m_lstMembers.Clear();
			}

			// tell all players
			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				UserSession? session = member.GetAssociatedSession();
				if (session != null)
				{
					await SendSearching(session, CurrentMemberCount(), DesiredPlayers, MinPlayers);
				}
			}
			return true;
		}

		public bool HasPlayer(UserSession playerSession)
		{
			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				if (member.Is(playerSession))
				{
					return true;
				}
			}

			return false;
		}

		public bool RemovePlayer(UserSession playerSession, out bool bCancellationRejected)
		{
			bCancellationRejected = false;
			lock (m_StateLock)
			{
				foreach (MatchmakingBucketMember member in m_lstMembers)
				{
					if (member.Is(playerSession))
					{
						if (m_bStartCommitted)
						{
							bCancellationRejected = true;
							return false;
						}

						if (m_bCreatingLobby || m_bWaitingOnLobbyJoins || m_bHasStartedCountdown || m_bWaitingOnMeshConnectivityChecks)
						{
							m_bAutoStartInvalidated = true;
						}

						m_lstMembers.Remove(member);
						return true;
					}
				}
			}

			return false;
		}

		public int CurrentMemberCount()
		{
			return m_lstMembers.Count;
		}

		// members whose UserSession has been collected/disconnected are dead weight - they inflate the member count,
		// which both blocks the "everyone joined the lobby" check and skews the average elo
		public bool PruneDeadMembers()
		{
			bool bInvalidatedSetup = false;
			lock (m_StateLock)
			{
				foreach (MatchmakingBucketMember member in m_lstMembers)
				{
					if (member.GetAssociatedSession() == null)
					{
						if (m_bCreatingLobby || m_bWaitingOnLobbyJoins || m_bHasStartedCountdown || m_bWaitingOnMeshConnectivityChecks)
						{
							m_bAutoStartInvalidated = true;
							bInvalidatedSetup = true;
						}

						m_lstMembers.Remove(member);
					}
				}
			}

			return bInvalidatedSetup;
		}

		internal void MarkPendingDeletion()
		{
			lock (m_StateLock)
			{
				m_bPendingDeletion = true;
			}
		}

		// TODO_EFCORE: Shared User data, and session<->websocket could be weakrefs
		public bool IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(UserSession? joiningUserSession, Int64 joining_user)
		{
			// NOTE: We check blocking in both directions, joiner blocked them, or joiner is blocked by a player
			SharedUserData? joiningUserData = joiningUserSession != null
				? GenOnlineService.WebSocketManager.GetSharedDataForUser(joiningUserSession.m_UserID)
				: null;

			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				UserSession? memberSession = member.GetAssociatedSession();
				if (memberSession != null)
				{
					SharedUserData? memberUserData = GenOnlineService.WebSocketManager.GetSharedDataForUser(memberSession.m_UserID);

					if (memberUserData != null)
					{
						// Bucket member has blocked the joining user
						if (memberUserData.GetSocialContainer().Blocked.Contains(joining_user))
							return true;

						// Joining user has blocked the bucket member
						if (joiningUserData?.GetSocialContainer().Blocked.Contains(memberSession.m_UserID) == true)
							return true;
					}
				}
			}

			return false;
		}

		public bool HasSpaceForUsers(int numUsers, GameBuild build)
		{
			// stale buckets must never accept new members
			if (IsMergedAway() || IsPendingDeletion())
			{
				return false;
			}

			// if we're already counting down, dont let people join, just pretend we are full
			if (IsLockedForMatchStart())
			{
				return false;
			}

			// the game, crcs and anticheat must all match
			if (build != Build)
			{
				return false;
			}

			return numUsers <= (DesiredPlayers - m_lstMembers.Count);
		}

		public bool IsAvgEloWithinThreshold(int playerElo, int eloThreshold)
		{
            // calculate an average elo if it's a team game, othewrise average will just be the other player
            int avgElo = GetAvgElo();

            int lowerEloBound = playerElo - eloThreshold;
            int upperEloBound = playerElo + eloThreshold;

            return (avgElo >= lowerEloBound && avgElo <= upperEloBound);
        }

		public int GetAvgElo()
		{
			int numMembers = m_lstMembers.Count;

			if (numMembers == 0)
			{
				return EloConfig.BaseRating;
			}

            int avgElo = 0;
			int numContributingMembers = 0;
            foreach (MatchmakingBucketMember member in m_lstMembers)
            {
				UserSession? memberSession = member.GetAssociatedSession();
                if (memberSession != null)
                {
					SharedUserData? memberUserData = GenOnlineService.WebSocketManager.GetSharedDataForUser(memberSession.m_UserID);

					if (memberUserData?.GameStats != null)
					{
						avgElo += MatchmakingManager.GetMatchmakingElo(memberUserData.GameStats);
						++numContributingMembers;
					}
                }
            }

			// only divide by the members we actually got an elo for, otherwise dead/unknown sessions drag the average down
			if (numContributingMembers == 0)
			{
				return EloConfig.BaseRating;
			}

            avgElo /= numContributingMembers;

			return avgElo;
        }


		public async Task<bool> JoinAsync(UserSession playerSession)
		{
			lock (m_StateLock)
			{
				if (HasPlayer(playerSession)) return true;
				if (playerSession.IsRetired || IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(playerSession, playerSession.m_UserID)
					|| !HasSpaceForUsers(1, playerSession.MatchmakingBuild)) return false;
				m_lstMembers.Add(new MatchmakingBucketMember(playerSession));
			}
			foreach (var member in m_lstMembers)
			{
				var session = member.GetAssociatedSession();
				if (session != null)
					await SendSearching(session, CurrentMemberCount(), DesiredPlayers, MinPlayers);
			}
			return true;
		}

		public MatchmakingBucket(UInt16 playlistID, UserSession owningSession, int minPlayers, int desiredPlayers, ConcurrentList<int> mapIndices, GameBuild build)
		{
			PlaylistID = playlistID;
			MinPlayers = minPlayers;
			DesiredPlayers = desiredPlayers;
			lstMapIndices = mapIndices;
			Build = build;

			m_lstMembers.Add(new MatchmakingBucketMember(owningSession));
		}

		public Int64 GetLobbyID()
		{
			return m_LobbyID;
		}

		Int64 m_LobbyID = -1;
		Int64 m_StartTime = -1;
		Int64 m_timeStartedWaitingOnLobbyJoins = -1;

		// how long we give everyone to actually connect to the QuickMatch lobby before we give up on the stragglers
		private const Int64 LobbyJoinTimeoutMs = 45000;
		private const int GameStartCountdownMs = 5000;
		private const int SetupClientTimeoutMarginMs = 2000;

		private async Task StartGameAfterSuccessfulMeshCheck(Lobby lobby)
		{
			s_log.LogInformation("Starting QuickMatch game for lobby {LobbyId}", lobby.LobbyID);
			AppMetrics.RecordMatchmakingMatch(PlaylistID.ToString(CultureInfo.InvariantCulture), "started");

			// MatchStart must precede any LobbyState with start, clients treat a quick match start as MatchStart too
			LobbyIdEvent matchStart = new LobbyIdEvent { LobbyId = lobby.LobbyID };
			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				UserSession? memberSession = member.GetAssociatedSession();
				if (memberSession != null)
				{
					MatchmakingManager.RecordQueueExit(memberSession, "matched");
					memberSession.IsRegisteredForMatchmaking = false;
					RealtimeOut.Fire(RealtimeOut.UserGame(memberSession.m_UserID).MatchStart(matchStart));
				}
			}

			// the match id and seed reach quick match players the same way as custom lobbies
			await lobby.UpdateState(ELobbyState.INGAME);
			await lobby.BroadcastStartGameAsync();
			MatchmakingManager.DestroyBucket(this);
		}

		private void QueueSetupProgress(int timeoutMSec, int countdownMSec = 0)
		{
			MatchSetupProgressEvent setupProgress = new MatchSetupProgressEvent { TimeoutS = (timeoutMSec + 999) / 1000, CountdownS = (countdownMSec + 999) / 1000 };
			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				UserSession? memberSession = member.GetAssociatedSession();
				if (memberSession != null)
				{
					RealtimeOut.Fire(RealtimeOut.UserGame(memberSession.m_UserID).MatchSetupProgress(setupProgress));
				}
			}
		}

		private long m_MeshCheckID;

		private async Task TriggerFullMeshConnectivityChecks(Lobby lobby)
		{
			m_MeshCheckID = await lobby.StartMeshCheckAsync();
			lock (m_StateLock)
			{
				// Publish the state transition before any notification work. If a send fails, the
				// regular lobby timeout still completes or aborts the setup instead of stranding it.
				m_bWaitingOnMeshConnectivityChecks = true;
			}

			QueueSetupProgress(FullMeshCheckSettings.MaxDurationMs + SetupClientTimeoutMarginMs);

			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				UserSession? memberSession = member.GetAssociatedSession();
				if (memberSession != null)
				{
					await SendStatus(memberSession, StatusCodes.Preparing);
				}
			}
		}

		private async Task AbortQuickMatchAutoStartAsync(MatchRequeueReason reason, bool dropNonJoiners = false)
		{
			// a lobby handed to another copy is frozen, the abort and its requeues wait or end with the bucket released
			LobbyManager lobbies = ServiceLocator.Services.GetRequiredService<LobbyManager>();
			Lobby? held = m_LobbyID == -1 ? null : lobbies.GetLobby(m_LobbyID);
			if (held != null && !await held.TryReserveForStartAsync())
			{
				return;
			}
			if (held == null && FrozenOrReleasedByHandover(lobbies))
			{
				return;
			}
			try
			{
				List<UserSession> sessionsToRequeue = new();
				long abortedLobbyID;
				lock (m_StateLock)
				{
					if (m_bAbortInProgress || m_bStartCommitted)
					{
						return;
					}

					m_bAbortInProgress = true;
					abortedLobbyID = m_LobbyID;
					m_bPendingDeletion = true;

					foreach (MatchmakingBucketMember member in m_lstMembers)
					{
						UserSession? memberSession = member.GetAssociatedSession();
						if (memberSession != null)
						{
							sessionsToRequeue.Add(memberSession);
						}
					}

					m_StartTime = -1;
					m_bWaitingOnLobbyJoins = false;
					m_bHasStartedCountdown = false;
					m_bWaitingOnMeshConnectivityChecks = false;
				}
				AppMetrics.RecordMatchmakingMatch(PlaylistID.ToString(CultureInfo.InvariantCulture), "setup_aborted");

				LobbyManager lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
				Lobby? quickMatchLobby = lobbyManager.GetLobby(abortedLobbyID);
				if (quickMatchLobby != null)
				{
					if (dropNonJoiners)
					{
						// Close admission before classifying late arrivals; never change a newer registration.
						await quickMatchLobby.UpdateState(ELobbyState.COMPLETE);
						foreach (var session in sessionsToRequeue)
						{
							if (quickMatchLobby.GetMemberFromUserID(session.m_UserID) != null) continue;
							bool dropped = false;
							await session.MatchmakingStateLock.WaitAsync();
							try
							{
								lock (m_StateLock)
								{
									if (HasPlayer(session))
									{
										MatchmakingManager.RecordQueueExit(session, "join_timeout");
										session.IsRegisteredForMatchmaking = false;
										dropped = true;
									}
								}
							}
							finally { session.MatchmakingStateLock.Release(); }
							if (dropped) await SendLeft(session, "join_timeout");
						}
					}
					foreach (UserSession memberSession in sessionsToRequeue)
					{
						LobbyMember? lobbyMember = quickMatchLobby.GetMemberFromUserID(memberSession.m_UserID);
						if (lobbyMember != null)
						{
							// the departing player also drops its peers and anticheat registrations before the next lobby
							await lobbyManager.RemoveMatchmakingMemberAsync(quickMatchLobby, lobbyMember, sendPeerTeardown: true);
						}
					}

					await lobbyManager.DeleteLobby(quickMatchLobby);
				}

				foreach (UserSession memberSession in sessionsToRequeue)
				{
					await lobbyManager.TryRequeueMatchmakingAsync(memberSession, abortedLobbyID, reason);
				}

				m_lstMembers.Clear();
				m_LobbyID = -1;

				MatchmakingManager.DestroyBucket(this);
			}
			finally
			{
				held?.ReleaseStartReservation();
			}
		}

		public async Task Tick()
		{
			bool bPendingDeletion;
			bool bAutoStartInvalidated;
			bool bWaitingOnMeshConnectivityChecks;
			bool bWaitingOnLobbyJoins;
			bool bHasStartedCountdown;
			lock (m_StateLock)
			{
				if (m_bCreatingLobby) return;
				bPendingDeletion = m_bPendingDeletion;
				bAutoStartInvalidated = m_bAutoStartInvalidated;
				bWaitingOnMeshConnectivityChecks = m_bWaitingOnMeshConnectivityChecks;
				bWaitingOnLobbyJoins = m_bWaitingOnLobbyJoins;
				bHasStartedCountdown = m_bHasStartedCountdown;
			}

			// merged/deleted buckets must not create another lobby or continue a committed setup
			if (IsMergedAway() || bPendingDeletion)
			{
				return;
			}

			var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();

			// a quick match lobby handed to another copy freezes its setup here, once it left the copy that took it carries it on
			if (FrozenOrReleasedByHandover(lobbyManager))
			{
				return;
			}

			// drop any members whose session has gone away, otherwise they are counted forever and the bucket can
			// never reach the "everyone is in the lobby" condition
			if (PruneDeadMembers())
			{
				bAutoStartInvalidated = true;
			}

			// TODO_QUICKMATCH: What if the playlist is null? is this even possible since we validated before creating the bucket
			if (g_Playlists.TryGetValue(PlaylistID, out Playlist? playlist))
			{
				// nobody left? clean ourselves up rather than lingering as a ghost bucket
				if (CurrentMemberCount() == 0 && !bWaitingOnLobbyJoins && !bHasStartedCountdown)
				{
					MatchmakingManager.DestroyBucket(this);
					return;
				}

				if (bAutoStartInvalidated)
				{
					await AbortQuickMatchAutoStartAsync(MatchRequeueReason.PlayerLeft);
					return;
				}

				if (bWaitingOnMeshConnectivityChecks)
				{
					Lobby? lobbyDuringMeshCheck = lobbyManager.GetLobby(m_LobbyID);
					if (lobbyDuringMeshCheck == null)
					{
						if (FrozenOrReleasedByHandover(lobbyManager))
						{
							return;
						}
						await AbortQuickMatchAutoStartAsync(MatchRequeueReason.LobbyGone);
						return;
					}

					await lobbyDuringMeshCheck.ProcessPendingFullMeshConnectivityChecks();

					if (await lobbyDuringMeshCheck.GetValidatedMeshCheckResultAsync(m_MeshCheckID) is bool meshComplete)
					{
						// a forced move that won the lobby gate during the await freezes the setup, the move then carries it on
						if (!await lobbyDuringMeshCheck.TryReserveForStartAsync())
						{
							return;
						}
						try
						{
							bool bStartCountdown;
							bool bAbortStart;
							bool bInvalidatedAtDecision;
							lock (m_StateLock)
							{
								bInvalidatedAtDecision = m_bAutoStartInvalidated;
								if (m_bStartCommitted || m_bAbortInProgress || m_bPendingDeletion)
								{
									bStartCountdown = false;
									bAbortStart = false;
								}
								else
								{
									m_bWaitingOnMeshConnectivityChecks = false;
									bStartCountdown = IsFormationCurrent(lobbyDuringMeshCheck) && meshComplete;
									bAbortStart = !bStartCountdown;
									if (bStartCountdown)
									{
										m_StartTime = Environment.TickCount64 + GameStartCountdownMs;
										m_bHasStartedCountdown = true;
									}
								}
							}

							if (bStartCountdown)
							{
								QueueSetupProgress(GameStartCountdownMs + SetupClientTimeoutMarginMs, GameStartCountdownMs);
								foreach (MatchmakingBucketMember member in m_lstMembers)
								{
									UserSession? memberSession = member.GetAssociatedSession();
									if (memberSession != null)
									{
										await SendStatus(memberSession, StatusCodes.Starting, new { Seconds = GameStartCountdownMs / 1000 });
									}
								}
							}
							else if (bAbortStart)
							{
								await AbortQuickMatchAutoStartAsync(bInvalidatedAtDecision ? MatchRequeueReason.PlayerLeft : MatchRequeueReason.MeshIncomplete);
							}
						}
						finally
						{
							lobbyDuringMeshCheck.ReleaseStartReservation();
						}
					}

					return;
				}

				if (bHasStartedCountdown)
				{
					if (Environment.TickCount64 < m_StartTime)
					{
						return;
					}

					Lobby? lobbyAfterCountdown = lobbyManager.GetLobby(m_LobbyID);
					if (lobbyAfterCountdown == null)
					{
						if (FrozenOrReleasedByHandover(lobbyManager))
						{
							return;
						}
						await AbortQuickMatchAutoStartAsync(MatchRequeueReason.LobbyGone);
						return;
					}

					bool meshStillValid = await lobbyAfterCountdown.GetValidatedMeshCheckResultAsync(m_MeshCheckID) == true;
					if (!await lobbyAfterCountdown.TryReserveForStartAsync())
					{
						return;
					}
					try
					{
						bool bStartGame;
						bool bAbortStart;
						lock (m_StateLock)
						{
							if (m_bStartCommitted || m_bAbortInProgress || m_bPendingDeletion)
							{
								bStartGame = false;
								bAbortStart = false;
							}
							else
							{
								m_bHasStartedCountdown = false;
								m_StartTime = -1;
								bStartGame = meshStillValid && IsFormationCurrent(lobbyAfterCountdown);
								bAbortStart = !bStartGame;
								if (bStartGame)
								{
									m_bStartCommitted = true;
									m_bPendingDeletion = true;
								}
							}
						}

						if (bStartGame)
						{
							await StartGameAfterSuccessfulMeshCheck(lobbyAfterCountdown);
						}
						else if (bAbortStart)
						{
							await AbortQuickMatchAutoStartAsync(MatchRequeueReason.PlayerLeft);
						}

					}
					finally
					{
						lobbyAfterCountdown.ReleaseStartReservation();
					}

					return;
				}

				// do we need to start?
				// TODO_MATCHMAKING: Add a timeout at which > min players starts
				if (!m_bWaitingOnLobbyJoins && !m_bHasStartedCountdown)
				{
					// must have a min player count
					if (MinPlayers != DesiredPlayers)
					{
						// have we hit the min player count? start a timer
						// NOTE: >= not ==, a merge (or several joins in one tick) can jump straight past MinPlayers
						if (!m_bReachedMinPlayers && CurrentMemberCount() >= MinPlayers)
						{
							m_bReachedMinPlayers = true;
							m_timeReachedMinPlayers = Environment.TickCount64;

							// tell players we reached min and will start in
							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								UserSession? memberSession = member.GetAssociatedSession();
								if (memberSession != null)
								{
									await SendStatus(memberSession, StatusCodes.GraceCountdown, new { Seconds = playlist.GracePeriodAtMinPlayersMSec / 1000 });
								}
							}
						}
						else if (m_bReachedMinPlayers && CurrentMemberCount() < MinPlayers) // did we go back under min players? stop countdown
						{
							m_bReachedMinPlayers = false;
							m_timeReachedMinPlayers = -1;

							// tell players we stopped
							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								UserSession? memberSession = member.GetAssociatedSession();
								if (memberSession != null)
								{
									await SendStatus(memberSession, StatusCodes.GraceCancelled);
								}
							}
						}
					}

                    // do we need an expansion of elo criteria?
                    if (TimeSinceLastEloExpansion().TotalSeconds >= EloConfig.SecondsBetweenEloExpansionsInMatchmaking)
                    {
                        // expand
                        ExpandElo();

						foreach (MatchmakingBucketMember member in m_lstMembers)
						{
							UserSession? memberSession = member.GetAssociatedSession();
							if (memberSession != null)
							{
								await SendStatus(memberSession, StatusCodes.Expanding);
							}
						}
                    }

                    // did we hit the timer OR have enough players to start?
                    bool bMinPlayersCountdownExpired = m_bReachedMinPlayers && (Environment.TickCount64 - m_timeReachedMinPlayers) > playlist.GracePeriodAtMinPlayersMSec;
					var formation = TryBeginFormation(playlist, bMinPlayersCountdownExpired);
					if (formation != null) await CreateFormationLobbyAsync(playlist, formation);
				}
				else
				{
					// do we need to cancel? people might have left
					// TODO_QUICKMATCH: Re-enable
					/*
					if (m_StartTime != -1 || m_bWaitingOnLobbyJoins)
					{
						if (CurrentMemberCount() < MinPlayers)
						{
							m_bWaitingOnLobbyJoins = false;
							m_bHasStartedCountdown = false;
							m_StartTime = -1;

							// destroy lobby
							Lobby? lobby = LobbyManager.GetLobby(m_LobbyID);
							if (lobby != null)
							{
								await LobbyManager.DeleteLobby(lobby);
							}

							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								ActiveUserDataCache? memberSession = member.GetAssociatedSession();
								if (memberSession != null)
								{
									await SendMatchmakingMessage(memberSession, String.Format("A player has left and the starting countdown has been cancelled. Status: {0}/{1} players. ({2} required to start)", CurrentMemberCount(), DesiredPlayers, MinPlayers));
								}
							}

							return;
						}
					}
					*/

					// waiting on lobby joins?
					if (m_bWaitingOnLobbyJoins)
					{
						// done? start time etc
						Lobby? lobby = lobbyManager.GetLobby(m_LobbyID);
						if (lobby == null)
						{
							// the lobby went away underneath us (deleted/failed) - don't leave everyone stuck waiting forever
							s_log.LogWarning("Matchmaking bucket lost QuickMatch lobby {LobbyId} while waiting on joins; abandoning bucket", m_LobbyID);

							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								UserSession? memberSession = member.GetAssociatedSession();
								if (memberSession != null)
								{
									MatchmakingManager.RecordQueueExit(memberSession, "setup_failed");
									memberSession.IsRegisteredForMatchmaking = false;
									await SendLeft(memberSession, "setup_failed");
								}
							}

							m_bWaitingOnLobbyJoins = false;
							m_timeStartedWaitingOnLobbyJoins = -1;
							MatchmakingManager.DestroyBucket(this);
							return;
						}

						// has someone failed to connect? drop them so the rest of the players aren't stuck here forever
						bool bJoinTimeoutExpired = m_timeStartedWaitingOnLobbyJoins != -1
							&& (Environment.TickCount64 - m_timeStartedWaitingOnLobbyJoins) > LobbyJoinTimeoutMs;

						if (bJoinTimeoutExpired && !IsFormationCurrent(lobby))
						{
							await AbortQuickMatchAutoStartAsync(MatchRequeueReason.JoinTimeout, dropNonJoiners: true);
							return;
						}

						{
							if (IsFormationCurrent(lobby))
							{
								m_timeStartedWaitingOnLobbyJoins = -1;

								lock (m_StateLock)
								{
									m_bWaitingOnLobbyJoins = false;
								}

								// finalize the teams
								const int playlistMaxPlayerPerTeam = 2;
								bool bIsFFA = true;
								int numTeams = lobby.NumCurrentPlayers / playlistMaxPlayerPerTeam;

								int teamID = 0;
								foreach (LobbyMember member in lobby.Members)
								{
									if (bIsFFA)
									{
										member.UpdateTeam(-1);
									}
									else
									{
										member.UpdateTeam(teamID);

										teamID++;

										if (teamID >= numTeams)
										{
											teamID = 0;
										}
									}
								}

								await TriggerFullMeshConnectivityChecks(lobby);
							}
						}

					}

				}
			}
		}

		// TODO_MATCHMAKING: Delete buckets if participants becomes 0
	}
	// Using ConcurrentBag instead of ConcurrentList for lock-free bucket management
	private static ConcurrentDictionary<UInt16, ConcurrentBag<MatchmakingBucket>> m_dictMatchmakingBuckets = new();

	public static void RemoveInvalidPlaylistsAtStartup()
	{
		foreach (var (id, playlist) in g_Playlists.ToArray())
		{
			string? error = null;
			if (playlist.MinPlayers < 2 || playlist.DesiredPlayers > Lobby.MaxLobbySize || playlist.MinPlayers > playlist.DesiredPlayers)
				error = "player bounds exceed supported lobby capacity";
			else if (playlist.MinSelectedMaps < 1 || playlist.MinSelectedMaps > playlist.Maps.Count)
				error = "minimum map selection is outside the playlist map count";
			else if (playlist.Maps.Any(map => map.MaxPlayers < playlist.MinPlayers || map.MaxPlayers > playlist.DesiredPlayers))
				error = "a map capacity is outside playlist player bounds";
			else if (Enumerable.Range(playlist.MinPlayers, playlist.DesiredPlayers - playlist.MinPlayers + 1)
				.Any(size => !playlist.Maps.Any(map => map.MaxPlayers == size)))
				error = "an allowed formation size has no exact-size map";
			if (error == null) continue;
			s_log.LogError("Disabling matchmaking playlist {PlaylistId} ({PlaylistName}): {Reason}", id, playlist.Name, error);
			g_Playlists.Remove(id);
		}
	}

	// TODO_QUICKMATCH: Read from db or file
	private static Dictionary<UInt16, Playlist> g_Playlists = new()
	{
		{ 0, new Playlist(0, "1v1 (Random Armies)", 2, 2, 1, false, -1, false, 0, new List<PlaylistMap>()
			{
				new PlaylistMap("[RANK] Snowy Drought ZH v5 (2)", "[RANK] Snowy Drought ZH v5", true, 2),
				new PlaylistMap("[RANK] Natural Threats ZH v4 (2)", "[RANK] Natural Threats ZH v4", true, 2),
				new PlaylistMap("[RANK] Arctic Lagoon ZH v2 (2)", "[RANK] Arctic Lagoon ZH v2", true, 2),
				new PlaylistMap("[RANK] ZH Carrier is Over v2 (2)", "[RANK] ZH Carrier is Over v2", true, 2),
				new PlaylistMap("[RANK] Vendetta ZH v1 (2)", "[RANK] Vendetta ZH v1", true, 2),
				new PlaylistMap("[RANK] TD NoBugsCars ZH v1 (2)", "[RANK] TD NoBugsCars ZH v1", true, 2),
				new PlaylistMap("[RANK] Sand Scorpion (2)", "[RANK] Sand Scorpion", true, 2),
				new PlaylistMap("[RANK] Mountain Mayhem v2 (2)", "[RANK] Mountain Mayhem v2", true, 2),
				new PlaylistMap("[RANK] Liquid Gold ZH v2 (2)", "[RANK] Liquid Gold ZH v2", true, 2),
				new PlaylistMap("[RANK] Imminent Victory ZH v2 (2)", "[RANK] Imminent Victory ZH v2", true, 2),
				new PlaylistMap("[RANK] Egyptian Oasis ZH v1 (2)", "[RANK] Egyptian Oasis ZH v1", true, 2),
				new PlaylistMap("[RANK] Desolated District ZH v1 (2)", "[RANK] Desolated District ZH v1", true, 2),
				new PlaylistMap("[RANK] Canyon of the Dead ZH v2 (2)", "[RANK] Canyon of the Dead ZH v2", true, 2),
				new PlaylistMap("[RANK] Blossoming Valley ZH v1 (2)", "[RANK] Blossoming Valley ZH v1", true, 2),
				new PlaylistMap("[RANK] Barren Badlands Balanced ZH v2 (2)", "[RANK] Barren Badlands Balanced ZH v2", true, 2),
				new PlaylistMap("Battle Plan ZH v3", "Battle Plan ZH v3", true, 2),
				new PlaylistMap("Canyon Frost ZH v1", "Canyon Frost ZH v1", true, 2),
				new PlaylistMap("Koujou Okawa v3", "Koujou Okawa v3", true, 2),
				new PlaylistMap("Oxygen 1", "Oxygen 1", true, 2),
				new PlaylistMap("Shivas Paradise v4", "Shivas Paradise v4", true, 2),
				new PlaylistMap("Thermopylae v5", "Thermopylae v5", true, 2),
				new PlaylistMap("Tiny Tactics ZH v2", "Tiny Tactics ZH v2", true, 2),
				new PlaylistMap("yota nation arena v3", "yota nation arena v3", true, 2),
				new PlaylistMap("[RANK] AKAs Magic ZH v1", "[RANK] AKAs Magic ZH v1", true, 2),
				new PlaylistMap("[RANK] Arctic Arena ZH v1", "[RANK] Arctic Arena ZH v1", true, 2),
				new PlaylistMap("[RANK] Black Hell ZH v1", "[RANK] Black Hell ZH v1", true, 2),
				new PlaylistMap("[RANK] Blue Hole ZH v1", "[RANK] Blue Hole ZH v1", true, 2),
				new PlaylistMap("[RANK] Dammed Scorpion ZH v1", "[RANK] Dammed Scorpion ZH v1", true, 2),
				new PlaylistMap("[RANK] Drallim Desert ZH v2", "[RANK] Drallim Desert ZH v2", true, 2),
				new PlaylistMap("[RANK] Farmlands of the Fallen ZH v1", "[RANK] Farmlands of the Fallen ZH v1", true, 2),
				new PlaylistMap("[RANK] Sakura Forest II ZH v1", "[RANK] Sakura Forest II ZH v1", true, 2),
				new PlaylistMap("[RANK] Sovereignty ZH v1", "[RANK] Sovereignty ZH v1", true, 2),
				new PlaylistMap("Arabia v2 (2)", "Arabia v2", true, 2),
				new PlaylistMap("SandForest Domination v5 (2)", "SandForest Domination v5", true, 2),
				new PlaylistMap("Toxic Lake v4 (2)", "Toxic Lake v4", true, 2),
				new PlaylistMap("Tremble v2 (2)", "Tremble v2", true, 2),
				new PlaylistMap("[RANK] Hanamura Temple ZH v1 (2)", "[RANK] Hanamura Temple ZH v1", true, 2),
				new PlaylistMap("Forest of Camelot ZH v4 (2)", "Forest of Camelot ZH v4", true, 2),
				new PlaylistMap("Mirmulnir v4 (2)", "Mirmulnir v4", true, 2),
				new PlaylistMap("Terraform v2 (2)", "Terraform v2", true, 2),
				new PlaylistMap("Toxic Lake v5 (2)", "Toxic Lake v5", true, 2)
			}
		) },

		/* PRE-WS list:
		 * { 0, new Playlist(0, "1v1 (Random Armies)", 2, 2, 1, false, -1, false, 0, new List<PlaylistMap>()
			{
				new PlaylistMap("[RANK] Snowy Drought ZH v5 (2)", "[RANK] Snowy Drought ZH v5", true, 2),
				new PlaylistMap("[RANK] Natural Threats ZH v4 (2)", "[RANK] Natural Threats ZH v4", true, 2),
				new PlaylistMap("[RANK] Arctic Lagoon ZH v2 (2)", "[RANK] Arctic Lagoon ZH v2", true, 2),
				new PlaylistMap("[RANK] ZH Carrier is Over v2 (2)", "[RANK] ZH Carrier is Over v2", true, 2),
				new PlaylistMap("[RANK] Vendetta ZH v1 (2)", "[RANK] Vendetta ZH v1", true, 2),
				new PlaylistMap("[RANK] TD NoBugsCars ZH v1 (2)", "[RANK] TD NoBugsCars ZH v1", true, 2),
				new PlaylistMap("[RANK] Sand Scorpion (2)", "[RANK] Sand Scorpion", true, 2),
				new PlaylistMap("[RANK] Mountain Mayhem v2 (2)", "[RANK] Mountain Mayhem v2", true, 2),
				new PlaylistMap("[RANK] Liquid Gold ZH v2 (2)", "[RANK] Liquid Gold ZH v2", true, 2),
				new PlaylistMap("[RANK] Imminent Victory ZH v2 (2)", "[RANK] Imminent Victory ZH v2", true, 2),
				new PlaylistMap("[RANK] Egyptian Oasis ZH v1 (2)", "[RANK] Egyptian Oasis ZH v1", true, 2),
				new PlaylistMap("[RANK] Desolated District ZH v1 (2)", "[RANK] Desolated District ZH v1", true, 2),
				new PlaylistMap("[RANK] Canyon of the Dead ZH v2 (2)", "[RANK] Canyon of the Dead ZH v2", true, 2),
				new PlaylistMap("[RANK] Blossoming Valley ZH v1 (2)", "[RANK] Blossoming Valley ZH v1", true, 2),
				//new PlaylistMap("[RANK] Battle Plan ZH v1 (2)", "[RANK] Battle Plan ZH v1", true, 2), // NOTE: Disabled. OldA say it is bugged according to map creator.
				new PlaylistMap("[RANK] Barren Badlands Balanced ZH v2 (2)", "[RANK] Barren Badlands Balanced ZH v2", true, 2),

				// new maps added in 12_05_25 update
				new PlaylistMap("Battle Plan ZH v3", "Battle Plan ZH v3", true, 2),
                new PlaylistMap("Canyon Frost ZH v1", "Canyon Frost ZH v1", true, 2),
                new PlaylistMap("Koujou Okawa v3", "Koujou Okawa v3", true, 2),
                new PlaylistMap("Oxygen 1", "Oxygen 1", true, 2),
                new PlaylistMap("Shivas Paradise v4", "Shivas Paradise v4", true, 2),
                new PlaylistMap("Thermopylae v5", "Thermopylae v5", true, 2),
                new PlaylistMap("Tiny Tactics ZH v2", "Tiny Tactics ZH v2", true, 2),
                new PlaylistMap("yota nation arena v3", "yota nation arena v3", true, 2),
                new PlaylistMap("[RANK] AKAs Magic ZH v1", "[RANK] AKAs Magic ZH v1", true, 2),
                new PlaylistMap("[RANK] Arctic Arena ZH v1", "[RANK] Arctic Arena ZH v1", true, 2),
                new PlaylistMap("[RANK] Black Hell ZH v1", "[RANK] Black Hell ZH v1", true, 2),
                new PlaylistMap("[RANK] Blue Hole ZH v1", "[RANK] Blue Hole ZH v1", true, 2),
                new PlaylistMap("[RANK] Dammed Scorpion ZH v1", "[RANK] Dammed Scorpion ZH v1", true, 2),
                new PlaylistMap("[RANK] Drallim Desert ZH v2", "[RANK] Drallim Desert ZH v2", true, 2),
                new PlaylistMap("[RANK] Farmlands of the Fallen ZH v1", "[RANK] Farmlands of the Fallen ZH v1", true, 2),
                new PlaylistMap("[RANK] Sakura Forest II ZH v1", "[RANK] Sakura Forest II ZH v1", true, 2),
                new PlaylistMap("[RANK] Sovereignty ZH v1", "[RANK] Sovereignty ZH v1", true, 2),

				// new maps added in 4_28_26 EAC update (for WS / from GR)
				new PlaylistMap("Arabia v2 (2)", "Arabia v2", true, 2),
				new PlaylistMap("SandForest Domination v5 (2)", "SandForest Domination v5", true, 2),
				new PlaylistMap("Toxic Lake v4 (2)", "Toxic Lake v4", true, 2),
				new PlaylistMap("Tremble v2 (2)", "Tremble v2", true, 2),
				new PlaylistMap("[RANK] Hanamura Temple ZH v1 (2)", "[RANK] Hanamura Temple ZH v1", true, 2),
			}
		) },
		*/

		{ 1, new Playlist(1, "6-8P FFA (Random Armies)", 6, 8, 1, false, -1, false, 30000, new List<PlaylistMap>()
			{
				new PlaylistMap("Beijing Uprise v4 (6)", "Beijing Uprise v4", true, 6),
				new PlaylistMap("Defcon6 (6)", "Defcon6", false, 6),
				new PlaylistMap("Taiga Terror (6)", "Taiga Terror", true, 6),
				new PlaylistMap("Swamp Assault v3 [WBC2021] (7)", "Swamp Assault v3 [WBC2021]", true, 7),
				new PlaylistMap("[RANK] Muddy Madness ZH v1 (8)", "[RANK] Muddy Madness ZH v1", true, 8),
				new PlaylistMap("[RANK] Wastelands Dust ZH v1 (8)", "[RANK] Wastelands Dust ZH v1", true, 8)
			}
		) }
	};

	public static Dictionary<UInt16, Playlist> GetPlaylists() { return g_Playlists; }

	public static int GetTotalQueuedPlayersInPlaylist(UInt16 playlistID)
	{
		int totalPlayers = 0;

		if (m_dictMatchmakingBuckets.ContainsKey(playlistID))
		{
			foreach (MatchmakingBucket bucket in m_dictMatchmakingBuckets[playlistID])
			{
				totalPlayers += bucket.CurrentMemberCount();
			}
		}

		return totalPlayers;
	}

	public static async Task Tick()
	{
		// TODO_QUICKMATCH: Move to init func, maybe dont use static for matchmakingmanager
		if (m_dictMatchmakingBuckets.Count == 0)
		{
			foreach (var kvPair in g_Playlists)
			{
				m_dictMatchmakingBuckets.TryAdd(kvPair.Key, new ConcurrentBag<MatchmakingBucket>());
			}
		}

		// tick mm buckets
		// TODO_QUICKMATCH: This is slow
		List<MatchmakingBucket> lstBucketsMergedNeedingDeleted = new(); // keeps deletion order
		HashSet<MatchmakingBucket> setBucketsMergedNeedingDeleted = new(); // O(1) membership checks for the same buckets
		foreach (var kvPair in m_dictMatchmakingBuckets)
		{
			// merging needs at least two buckets in the playlist; the bucket tick itself still runs for a lone bucket
			bool bCanMergeInPlaylist = kvPair.Value.Count >= 2 && FormsMatches;
			foreach (MatchmakingBucket mmBucket in kvPair.Value)
			{
				// if we've already been merged and are awaiting delayed deletion, dont process it anymore
				if (!setBucketsMergedNeedingDeleted.Contains(mmBucket) && !mmBucket.IsMergedAway())
				{
					await mmBucket.Tick();

					if (!bCanMergeInPlaylist)
					{
						continue;
					}

					// try to merge with any other bucket within this playlist
					foreach (MatchmakingBucket mmBucketMergeCandidate in kvPair.Value)
					{
						if (mmBucketMergeCandidate != mmBucket)
						{
							// if either bucket has already been merged and is awaiting delayed deletion, dont process it anymore
							if (setBucketsMergedNeedingDeleted.Contains(mmBucket) || mmBucket.IsMergedAway())
							{
								break;
							}

							if (!setBucketsMergedNeedingDeleted.Contains(mmBucketMergeCandidate) && !mmBucketMergeCandidate.IsMergedAway())
							{
								if (mmBucket.CanMergeWithOtherBucket(mmBucketMergeCandidate))
								{
									if (await mmBucket.MergeWithOtherBucketAsync(mmBucketMergeCandidate)
										&& setBucketsMergedNeedingDeleted.Add(mmBucketMergeCandidate))
									{
										lstBucketsMergedNeedingDeleted.Add(mmBucketMergeCandidate);
									}
								}
							}
						}
					}
				}
			}
		}

		// queue for deletion
		foreach (MatchmakingBucket bucket in lstBucketsMergedNeedingDeleted)
		{
			m_bucketsPendingDeletion.Enqueue(bucket);
		}

		// Drain the queue rather than enumerating and clearing a shared list. A concurrent cancellation can
		// enqueue a bucket while cleanup is running, and clearing the list would otherwise lose that request.
		while (m_bucketsPendingDeletion.TryDequeue(out MatchmakingBucket? bucket))
		{
			if (m_dictMatchmakingBuckets.TryGetValue(bucket.PlaylistID, out var bucketBag))
			{
				// ConcurrentBag doesn't support Remove, so we filter and rebuild
				var remainingBuckets = bucketBag.Where(b => b != bucket).ToList();
				m_dictMatchmakingBuckets[bucket.PlaylistID] = new ConcurrentBag<MatchmakingBucket>(remainingBuckets);
			}
		}

		List<WeakReference<UserSession>> lstDestroy = new();
		foreach (WeakReference<UserSession> wrSession in FormsMatches ? lstSessions : new ConcurrentList<WeakReference<UserSession>>())
		{
			if (!wrSession.TryGetTarget(out UserSession? thisSession) || thisSession == null)
			{
				lstDestroy.Add(wrSession);
			}
			else
			{
				await thisSession.MatchmakingStateLock.WaitAsync();
				try
				{
					// A cancellation can remove the weak reference while this tick is iterating a snapshot.
					// Re-check registration while holding the per-session gate before assigning any bucket.
					if (thisSession.IsRetired || !thisSession.IsRegisteredForMatchmaking || !IsPendingSession(thisSession))
					{
						lstDestroy.Add(wrSession);
						continue;
					}

					SharedUserData? thisSessionUserData = GenOnlineService.WebSocketManager.GetSharedDataForUser(thisSession.m_UserID);

					if (thisSessionUserData == null)
					{
						RecordQueueExit(thisSession, "session_unavailable");
						thisSession.IsRegisteredForMatchmaking = false;
						lstDestroy.Add(wrSession);
					}
					else
					{
						PlayerStats? thisSessionStats = thisSessionUserData.GameStats;
						if (thisSessionStats == null)
						{
							thisSession.IsRegisteredForMatchmaking = false;
							RecordQueueExit(thisSession, "stats_unavailable");
							lstDestroy.Add(wrSession);
							await SendLeft(thisSession, "stats_unavailable");
							continue;
						}

						if (g_Playlists.TryGetValue(thisSession.MatchmakingPlaylistID, out Playlist? playlist))
						{

						// TODO_MATCHAMAKING: Better way of tracking this, we need to know who is already in a bucket
						// Was the user in a bucket? if so theres nothing to do in terms of bucket management
						bool bUseInBucket = false;
						MatchmakingBucket? mmBucketUserIsIn = null;
						foreach (var kvBucketPair in m_dictMatchmakingBuckets)
						{
							foreach (MatchmakingBucket mmBucket in kvBucketPair.Value)
							{
								if (mmBucket.HasPlayer(thisSession))
								{
									bUseInBucket = true;
									mmBucketUserIsIn = mmBucket;
									break;
								}
							}

							if (bUseInBucket)
							{
								break;
							}
						}

						if (bUseInBucket)
						{
							// already sorted into a bucket - stop tracking this session here, otherwise a stale entry
							// can sort the same player into a second bucket (and therefore a second lobby) later on
							lstDestroy.Add(wrSession);
						}
						else
						{
							// is there a suitable bucket for us
							// TODO_MATCHMAKING: Optimize lookup
							if (m_dictMatchmakingBuckets.ContainsKey(thisSession.MatchmakingPlaylistID))
							{
								MatchmakingBucket? bucketInUse = null;
								foreach (MatchmakingBucket mmBucket in m_dictMatchmakingBuckets[thisSession.MatchmakingPlaylistID])
								{
									if (mmBucket.IsMergedAway())
									{
										continue;
									}

									// must be within initial elo threshold for a join, otherwise we'll make a bucket and try to merge buckets using the elo iteration expansion algorithm
									int matchmakingElo = MatchmakingManager.GetMatchmakingElo(thisSessionUserData.GameStats);
									int eloExpansionToUse = (mmBucket.GetAvgElo() >= EloConfig.HighEloThreshold || matchmakingElo >= EloConfig.HighEloThreshold) ? EloConfig.EloExpansionValue_HighELO : EloConfig.EloExpansionValue_Standard;
									if (mmBucket.IsAvgEloWithinThreshold(matchmakingElo, eloExpansionToUse))
									{
										// TODO_MATCHMAKING: Squads
										if (mmBucket.HasSpaceForUsers(1, thisSession.MatchmakingBuild))
										{
											// do the maps overlap? if so we can join
											if (mmBucket.DoMapSelectionsIntersect(thisSession.MatchmakingMapIndicies))
											{
												bool bJoined = await mmBucket.JoinAsync(thisSession);

												if (bJoined)
												{
													// stop looking - joining more than one bucket would matchmake this player into multiple lobbies
													bucketInUse = mmBucket;
													break;
												}
											}
										}
									}
								}

								// didnt find a bucket? make one
								if (bucketInUse == null)
								{
									MatchmakingBucket newBucket = new MatchmakingBucket(playlist.PlaylistID, thisSession, playlist.MinPlayers, playlist.DesiredPlayers, thisSession.MatchmakingMapIndicies, thisSession.MatchmakingBuild);
									m_dictMatchmakingBuckets[thisSession.MatchmakingPlaylistID].Add(newBucket);
									bucketInUse = newBucket;
								}

								// send status to use
								await SendSearching(thisSession, bucketInUse.CurrentMemberCount(), bucketInUse.DesiredPlayers, bucketInUse.MinPlayers);

								// now remove us from lstSessions, this list is essentially people who need sorted into a bucket
								lstDestroy.Add(wrSession);
							}
						}
						}
						else
						{
							// invalid playlist somehow
							RecordQueueExit(thisSession, "playlist_unavailable");
							thisSession.IsRegisteredForMatchmaking = false;
							lstDestroy.Add(wrSession);
							await SendLeft(thisSession, "invalid_playlist");
						}
					}
				}
				finally
				{
					thisSession.MatchmakingStateLock.Release();
				}
			}
		}

		// now remove
		foreach (WeakReference<UserSession> wrSession in lstDestroy)
		{
			if (wrSession != null)
			{
				lstSessions.Remove(wrSession);
			}
		}

		foreach (UInt16 playlistID in g_Playlists.Keys)
		{
			AppMetrics.RecordMatchmakingQueue(playlistID.ToString(CultureInfo.InvariantCulture), GetTotalQueuedPlayersInPlaylist(playlistID));
		}

		PublishLiveState();
	}

	// TODO_MATCHMAKING: Deregister player if they disconnect or leave quickmatch
	private static ConcurrentList<WeakReference<UserSession>> lstSessions = new();

	private static bool IsPendingSession(UserSession session)
	{
		foreach (WeakReference<UserSession> wrSession in lstSessions)
		{
			if (wrSession.TryGetTarget(out UserSession? pendingSession) && ReferenceEquals(pendingSession, session))
			{
				return true;
			}
		}

		return false;
	}

	private static void RemovePendingSession(UserSession session)
	{
		foreach (WeakReference<UserSession> wrSession in lstSessions.ToList())
		{
			if (!wrSession.TryGetTarget(out UserSession? pendingSession) || ReferenceEquals(pendingSession, session))
			{
				lstSessions.Remove(wrSession);
			}
		}
	}

	// records the wait of a queued player once; players that were not queued are ignored
	internal static void RecordQueueExit(UserSession session, string outcome)
	{
		long startedAt = Interlocked.Exchange(ref session.MatchmakingStartedTimestamp, 0);
		if (startedAt == 0) return;
		AppMetrics.RecordMatchmakingQueueExit(
			session.MatchmakingPlaylistID.ToString(CultureInfo.InvariantCulture),
			outcome,
			Stopwatch.GetElapsedTime(startedAt));
	}

	internal static async Task<bool> TryRequeueRegisteredPlayer(UserSession session, MatchRequeueReason reason)
	{
		await session.MatchmakingStateLock.WaitAsync();
		try
		{
			if (session.IsRetired || !session.IsRegisteredForMatchmaking)
			{
				return false;
			}

			if (!IsPendingSession(session))
			{
				lstSessions.Add(new WeakReference<UserSession>(session));
			}

			RealtimeOut.Fire(RealtimeOut.UserGame(session.m_UserID).MatchRequeue(new MatchRequeueEvent { Reason = reason }));
			return true;
		}
		finally
		{
			session.MatchmakingStateLock.Release();
		}
	}

	private static ConcurrentQueue<MatchmakingBucket> m_bucketsPendingDeletion = new();
	public static void DestroyBucket(MatchmakingBucket bucket)
	{
		bucket.MarkPendingDeletion();
		m_bucketsPendingDeletion.Enqueue(bucket);
	}

	internal enum ERegisterResult { Accepted, InvalidPlaylist, TooFewMaps, AlreadyQueued, StatsUnavailable, SessionUnavailable }

	public static int MinSelectedMapsOf(Playlist playlist) => Math.Max(1, playlist.MinSelectedMaps);

	internal static async Task<ERegisterResult> RegisterPlayer(UserSession plr, UInt16 playlistID, IReadOnlyList<int> mapIndices, GameBuild build)
	{
		using Activity? activity = AppMetrics.StartActivity("matchmaking.register");
		activity?.SetTag("user.id", plr.m_UserID);
		activity?.SetTag("matchmaking.playlist_id", playlistID);
		// validate the request - a bad playlist or out of range map index from a client must never reach a bucket
		if (!g_Playlists.TryGetValue(playlistID, out Playlist? playlist))
		{
			activity?.SetTag("matchmaking.outcome", "unavailable_playlist");
			s_log.LogWarning("User {UserId} requested unavailable matchmaking playlist {PlaylistId}", plr.m_UserID, playlistID);
			return ERegisterResult.InvalidPlaylist;
		}

		List<int> validatedMapIndices = mapIndices
			.Where(mapIndex => mapIndex >= 0 && mapIndex < playlist.Maps.Count)
			.Distinct()
			.ToList();

		int minSelectedMaps = MinSelectedMapsOf(playlist);
		if (validatedMapIndices.Count < minSelectedMaps)
		{
			activity?.SetTag("matchmaking.outcome", "insufficient_maps");
			s_log.LogWarning("User {UserId} selected {SelectedMapCount} valid maps for playlist {PlaylistId}; minimum is {MinimumMapCount}", plr.m_UserID, validatedMapIndices.Count, playlistID, minSelectedMaps);
			return ERegisterResult.TooFewMaps;
		}

		if (GenOnlineService.WebSocketManager.GetSharedDataForUser(plr.m_UserID)?.GameStats == null)
		{
			activity?.SetTag("matchmaking.outcome", "stats_unavailable");
			return ERegisterResult.StatsUnavailable;
		}

		MatchmakingDetachment detached;
		bool wasQueued;
		await plr.MatchmakingStateLock.WaitAsync();
		try
		{
			if (plr.IsRetired) return ERegisterResult.SessionUnavailable;
			if (plr.IsRegisteredForMatchmaking)
			{
				activity?.SetTag("matchmaking.outcome", "already_queued");
				return ERegisterResult.AlreadyQueued;
			}
			wasQueued = plr.MatchmakingStartedTimestamp != 0;
			plr.IsRegisteredForMatchmaking = false;
			RemovePendingSession(plr);
			detached = RemovePlayerFromAllBuckets(plr);

			if (!detached.CancellationRejected)
			{
				if (wasQueued)
				{
					RecordQueueExit(plr, "restarted");
				}
				plr.MatchmakingPlaylistID = playlistID;
				plr.MatchmakingMapIndicies = new ConcurrentList<int>(validatedMapIndices);
				plr.MatchmakingBuild = build;
				plr.IsRegisteredForMatchmaking = true;
				plr.MatchmakingStartedTimestamp = Stopwatch.GetTimestamp();
				lstSessions.Add(new WeakReference<UserSession>(plr));
			}
		}
		finally
		{
			plr.MatchmakingStateLock.Release();
		}

		var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
		foreach (var entry in detached.Members)
			await lobbyManager.RemoveMatchmakingMemberAsync(entry.Lobby, entry.Member);

		if (detached.CancellationRejected)
		{
			activity?.SetTag("matchmaking.outcome", "start_in_progress");
			s_log.LogWarning("User {UserId} could not restart matchmaking because the game is starting", plr.m_UserID);
			return ERegisterResult.AlreadyQueued;
		}

		activity?.SetTag("matchmaking.outcome", "accepted");
		s_log.LogInformation("User {UserId} started matchmaking in playlist {PlaylistId} with {SelectedMapCount} maps", plr.m_UserID, playlistID, validatedMapIndices.Count);
		await SendStatus(plr, StatusCodes.Queued, new { PlaylistId = (int)playlistID });
		RealtimeHub.PublishPresence(plr.m_UserID);
		return ERegisterResult.Accepted;
	}

	internal sealed record MatchmakingDetachment(bool CancellationRejected, List<(Lobby Lobby, LobbyMember Member)> Members);

	private static MatchmakingDetachment RemovePlayerFromAllBuckets(UserSession player)
	{
		var lobbies = ServiceLocator.Services.GetRequiredService<LobbyManager>();
		bool rejected = false;
		List<(Lobby, LobbyMember)> members = new();
		foreach (var buckets in m_dictMatchmakingBuckets.Values)
		{
			foreach (var bucket in buckets)
			{
				bool removed = bucket.RemovePlayer(player, out bool cancellationRejected);
				rejected |= cancellationRejected;
				if (!removed) continue;
				var lobby = lobbies.GetLobby(bucket.GetLobbyID());
				var member = lobby?.GetMemberFromUserID(player.m_UserID);
				if (member != null && member.GetSession().TryGetTarget(out var memberSession) && ReferenceEquals(memberSession, player))
					members.Add((lobby!, member));
				if (bucket.CurrentMemberCount() == 0) DestroyBucket(bucket);
			}
		}
		return new(rejected, members);
	}

	// Only matchmaking state is changed here. Membership effects run after releasing
	// this lock, so lifecycle operations can acquire user gate -> matchmaking lock.
	internal static async Task<MatchmakingDetachment> DetachSessionAsync(UserSession player)
	{
		await player.MatchmakingStateLock.WaitAsync();
		try
		{
			player.IsRegisteredForMatchmaking = false;
			RemovePendingSession(player);
			var detached = RemovePlayerFromAllBuckets(player);
			if (!detached.CancellationRejected) RecordQueueExit(player, "withdrawn");
			return detached;
		}
		finally
		{
			player.MatchmakingStateLock.Release();
			// a player switching to another copy shows no change to friends, the new connection publishes presence there
			if (!player.SwitchingAway && !GenOnlineService.Failover.RealtimeFailover.Fenced)
			{
				RealtimeHub.PublishPresence(player.m_UserID);
			}
		}
	}

	public static Task DeregisterPlayerAsync(UserSession player) =>
		ServiceLocator.Services.GetRequiredService<LobbyManager>().CancelMatchmakingAsync(player);
}
