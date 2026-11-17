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

using GenOnlineService.Failover;
using GenOnlineService.Realtime;
using System.Globalization;

namespace GenOnlineService
{
	// The live state of one lobby for docs/realtime-failover.md build step 3: captured under the lobby gate on the owner, restored on the copy that takes over
	public partial class Lobby
	{
		// Set by every change DirtyRetransmit sees and by the connection bookkeeping, read by the tick
		private int m_LiveDirty = 1;
		private int m_LiveRemoved;
		private long m_LiveNextRefresh;
		private long m_LiveNextRewrite;
		private int m_LiveGeneration = -1;
		private string? m_LivePublished;

		// Set while a planned handover gives this lobby to another copy, the tick then writes nothing of it
		private int m_MovingAway;

		internal bool IsMovingAway => Volatile.Read(ref m_MovingAway) != 0;

		// A quick match start or abort that decided under the gate holds the lobby, a forced move waits for it
		private int m_StartReservations;

		// True when the start or abort may go on: the lobby is not moving and no move begins until the release
		internal Task<bool> TryReserveForStartAsync() => RunExclusiveAsync(() =>
		{
			if (IsMovingAway)
			{
				return Task.FromResult(false);
			}
			Interlocked.Increment(ref m_StartReservations);
			return Task.FromResult(true);
		});

		internal void ReleaseStartReservation() => Interlocked.Decrement(ref m_StartReservations);

		// A countdown restored from another copy runs to its deadline without the mesh watch, the members reconnect meanwhile
		private bool m_RestoredCountdown;

		private void MarkLiveDirty() => Volatile.Write(ref m_LiveDirty, 1);

		// Process counters start at a time based value, so a check or probe id of an earlier copy never matches one of this copy
		private static Int64 ProcessCounterStart() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

		// The tick seam: a changed lobby, a new generation or the refresh interval captures the state under the gate and queues it outside, only when it differs
		private async Task PublishLiveStateAsync()
		{
			if (!RealtimeLiveState.Writing || Volatile.Read(ref m_LiveRemoved) != 0 || IsMovingAway)
			{
				return;
			}

			long now = Environment.TickCount64;
			int generation = RealtimeLiveState.Generation;
			bool dirty = Interlocked.Exchange(ref m_LiveDirty, 0) != 0;
			if (!dirty && generation == m_LiveGeneration && now < m_LiveNextRefresh)
			{
				return;
			}

			LobbyLive live = await RunExclusiveAsync(() => Task.FromResult(CaptureLiveLocked()));
			string json = LiveJson.Data(live);
			m_LiveNextRefresh = now + RealtimeLiveState.RefreshMs;

			// an unchanged lobby is written again only when rewrite_ms asks for it, compare on write keeps late writes out
			bool rewrite = RealtimeLiveState.RewriteMs > 0 && now >= m_LiveNextRewrite;
			if (!rewrite && generation == m_LiveGeneration && String.Equals(json, m_LivePublished, StringComparison.Ordinal))
			{
				return;
			}

			m_LiveNextRewrite = now + RealtimeLiveState.RewriteMs;
			m_LivePublished = json;
			m_LiveGeneration = generation;
			RealtimeLiveState.Set(LiveKeys.Lobby(LobbyID), json);

			// a delete that raced this write is queued again behind it
			if (Volatile.Read(ref m_LiveRemoved) != 0)
			{
				RealtimeLiveState.Delete(LiveKeys.Lobby(LobbyID));
			}
		}

		// A lobby that left this copy without being destroyed writes nothing more, its stored state belongs to another owner
		internal void MarkReplaced() => Volatile.Write(ref m_LiveRemoved, 1);

		// The removal of a destroyed lobby, written once
		internal void PublishLiveRemoval()
		{
			if (Interlocked.Exchange(ref m_LiveRemoved, 1) == 0)
			{
				RealtimeLiveState.Delete(LiveKeys.Lobby(LobbyID));
			}
		}

		private static LiveBuild LiveBuildOf(GameBuild build) => new LiveBuild { Game = build.Game, ExeCrc = build.ExeCRC, IniCrc = build.IniCRC, AnticheatId = (int)build.AnticheatID };

		private static GameBuild GameBuildOf(LiveBuild build) => new GameBuild(build.Game ?? String.Empty, build.ExeCrc, build.IniCrc, (EKnownAnticheatID)build.AnticheatId);

		private static string Id(Int64 value) => value.ToString(CultureInfo.InvariantCulture);

		// Must run under the lobby gate
		internal LobbyLive CaptureLiveLocked()
		{
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			LobbyStartData? start = m_StartData;
			EStartPhase phase = m_StartPhase;
			return new LobbyLive
			{
				Id = LobbyID,
				Type = (int)LobbyType,
				State = (int)State,
				Owner = Owner,
				Name = Name,
				MapName = MapName,
				MapPath = MapPathWire,
				MapOfficial = IsMapOfficial,
				MatchId = MatchID,
				CreatedAt = TimeCreated,
				VanillaTeams = IsVanillaTeamsOnly,
				StartingCash = StartingCash,
				LimitSuperweapons = IsLimitSuperweapons,
				TrackStats = IsTrackingStats,
				Passworded = IsPassworded,
				Password = Password,
				AllowObservers = AllowObservers,
				AllowArmySelection = AllowArmySelection,
				PlaylistId = PlaylistID,
				Build = LiveBuildOf(Build),
				NetworkRoomId = NetworkRoomID,
				RngSeed = RNGSeed,
				Region = Region,
				Latitude = m_dHostLatitude,
				Longitude = m_dHostLongitude,
				MaxCameraHeight = MaximumCameraHeight,
				Joinability = (int)LobbyJoinability,
				Slots = Members.Select(member => new LobbyMemberLive
				{
					SlotIndex = member.SlotIndex,
					SlotState = (int)member.SlotState,
					UserId = member.UserID,
					DisplayName = member.DisplayName,
					NameNotDeduped = member.DisplayNameNotDeduped,
					NameTag = member.NameTag,
					Ready = member.IsReadyStored,
					Port = member.Port,
					Side = member.Side,
					Team = member.Team,
					Color = member.Color,
					StartPosition = member.StartingPosition,
					HasMap = member.HasMap,
					Region = member.Region
				}).ToList(),
				EverMembers = m_EverMemberUserIDs.Keys.OrderBy(id => id).ToList(),
				MemberLeftAt = TimeMemberLeft.OrderBy(entry => entry.Key).ToDictionary(entry => Id(entry.Key), entry => entry.Value),
				MatchedUserIds = MatchedUserIDs.OrderBy(id => id).ToList(),
				StartPhase = phase switch { EStartPhase.Countdown => "countdown", EStartPhase.MeshCheck => "mesh_check", _ => "none" },
				CountdownEndsAtMs = phase == EStartPhase.Countdown && m_TimeCountdownEnds > 0 ? nowMs + Math.Max(0, m_TimeCountdownEnds - Environment.TickCount64) : null,
				Start = start == null ? null : new LobbyStartLive
				{
					MatchId = start.MatchId,
					RngSeed = start.RngSeed,
					ScreenshotUrls = start.ScreenshotURLs.OrderBy(entry => entry.Key).ToDictionary(entry => Id(entry.Key), entry => entry.Value)
				},
				Mesh = new LobbyMeshLive
				{
					CheckId = FullMeshCheckID,
					Attempt = FullMeshCheckAttempt,
					LastOutcome = LastFullMeshConnectivityCheckOutcome,
					LastFailureReason = LastFullMeshCheckFailureReason,
					Pending = PendingFullMeshConnectivityChecks
				},
				ReportedConnections = m_ReportedConnections.Keys.OrderBy(pair => pair.Reporter).ThenBy(pair => pair.Peer).Select(pair => new[] { pair.Reporter, pair.Peer }).ToList(),
				PeerPairs = m_PeerPairs.All().OrderBy(pair => pair.Low).ThenBy(pair => pair.High).Select(pair => new PeerPairLive { Low = pair.Low, High = pair.High, Attempt = pair.State.Attempt, Step = pair.State.Step }).ToList(),
				AtStart = m_cachedAtStart_numHumans < 0 ? null : new LobbyCountsLive { Humans = m_cachedAtStart_numHumans, Ai = m_cachedAtStart_numAI, Open = m_cachedAtStart_numOpen, Closed = m_cachedAtStart_numClosed },
				AbandonedInGame = TimePlayerAbandonedIngame.OrderBy(entry => entry.Key).ToDictionary(entry => Id(entry.Key), entry => entry.Value),
				ReportedOutcomes = ReportedOutcomes.OrderBy(entry => entry.Key).ToDictionary(entry => Id(entry.Key), entry => entry.Value),
				ProbesEnabled = m_NextProbe != 0
			};
		}

		private static Int64 ParseID(string text) => Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int64 id) ? id : throw new FormatException($"'{text}' is no user id");

		// Why a stored lobby is not restored: a finished lobby, or a quick match setup that the queue restores as tickets
		internal static string? SkipReasonOf(LobbyLive live)
		{
			ELobbyState state = (ELobbyState)live.State;
			if (state == ELobbyState.COMPLETE || state == ELobbyState.UNKNOWN)
			{
				return "finished";
			}
			if ((ELobbyType)live.Type == ELobbyType.QuickMatch && state != ELobbyState.INGAME)
			{
				return "quick_match_setup";
			}
			return null;
		}

		// A live lobby from another copy's state: no member sessions yet, no pending checks or probes, the countdown continues and a mesh check goes back to setup
		internal static Lobby FromLive(LobbyLive live)
		{
			if (live.Id <= 0 || live.Slots.Count != MaxLobbySize || live.Slots.Select(slot => slot.SlotIndex).Distinct().Count() != MaxLobbySize
				|| live.Slots.Any(slot => slot.SlotIndex < 0 || slot.SlotIndex >= MaxLobbySize || !Enum.IsDefined(typeof(EPlayerType), slot.SlotState))
				|| !Enum.IsDefined(typeof(ELobbyState), live.State) || !Enum.IsDefined(typeof(ELobbyType), live.Type))
			{
				throw new FormatException($"Lobby {live.Id} has an unreadable slot layout or state");
			}

			Lobby lobby = new Lobby();
			lobby.LobbyID = live.Id;
			lobby.Owner = live.Owner;
			lobby.Name = live.Name ?? String.Empty;
			lobby.State = (ELobbyState)live.State;
			lobby.MapName = live.MapName ?? String.Empty;
			lobby.MapPath = FixMapPathForGame(live.MapPath ?? String.Empty);
			lobby.MapPathWire = live.MapPath ?? String.Empty;
			lobby.IsMapOfficial = live.MapOfficial;
			lobby.MatchID = live.MatchId;
			lobby.TimeCreated = live.CreatedAt;
			lobby.IsVanillaTeamsOnly = live.VanillaTeams;
			lobby.StartingCash = live.StartingCash;
			lobby.IsLimitSuperweapons = live.LimitSuperweapons;
			lobby.IsTrackingStats = live.TrackStats;
			lobby.IsPassworded = live.Passworded;
			lobby.Password = live.Password ?? String.Empty;
			lobby.AllowObservers = live.AllowObservers;
			lobby.AllowArmySelection = live.AllowArmySelection;
			lobby.PlaylistID = live.PlaylistId;
			GameBuild build = GameBuildOf(live.Build ?? new LiveBuild());
			lobby.Game = build.Game;
			lobby.ExeCRC = build.ExeCRC;
			lobby.IniCRC = build.IniCRC;
			lobby.AnticheatID = build.AnticheatID;
			lobby.NetworkRoomID = live.NetworkRoomId;
			lobby.RNGSeed = live.RngSeed;
			lobby.Region = live.Region ?? String.Empty;
			lobby.m_dHostLatitude = live.Latitude;
			lobby.m_dHostLongitude = live.Longitude;
			lobby.MaximumCameraHeight = live.MaxCameraHeight;
			lobby.LobbyType = (ELobbyType)live.Type;
			lobby.LobbyJoinability = (ELobbyJoinability)live.Joinability;

			LobbyMember[] members = new LobbyMember[MaxLobbySize];
			foreach (LobbyMemberLive slot in live.Slots)
			{
				members[slot.SlotIndex] = LobbyMember.FromLive(lobby, slot);
			}
			lobby.Members = members;

			foreach (long userID in live.EverMembers ?? new())
			{
				lobby.m_EverMemberUserIDs[userID] = 0;
			}
			foreach ((string userID, DateTime at) in live.MemberLeftAt ?? new())
			{
				lobby.TimeMemberLeft[ParseID(userID)] = at;
			}
			foreach (long userID in live.MatchedUserIds ?? new())
			{
				lobby.MatchedUserIDs.Add(userID);
			}
			foreach ((string userID, DateTime at) in live.AbandonedInGame ?? new())
			{
				lobby.TimePlayerAbandonedIngame[ParseID(userID)] = at;
			}
			foreach ((string userID, bool won) in live.ReportedOutcomes ?? new())
			{
				lobby.ReportedOutcomes[ParseID(userID)] = won;
			}

			// pairs that reported connected need no new StartSignalling when their members reconnect
			foreach (long[] pair in live.ReportedConnections ?? new())
			{
				if (pair.Length == 2)
				{
					lobby.m_ReportedConnections[(pair[0], pair[1])] = 0;
				}
			}
			foreach (PeerPairLive pair in live.PeerPairs ?? new())
			{
				lobby.m_PeerPairs.Restore(pair.Low, pair.High, new PeerPairState(pair.Attempt, pair.Step));
			}

			if (live.AtStart != null)
			{
				lobby.m_cachedAtStart_numHumans = live.AtStart.Humans;
				lobby.m_cachedAtStart_numAI = live.AtStart.Ai;
				lobby.m_cachedAtStart_numOpen = live.AtStart.Open;
				lobby.m_cachedAtStart_numClosed = live.AtStart.Closed;
			}
			if (live.Start != null)
			{
				lobby.m_StartData = new LobbyStartData(live.Start.MatchId, live.Start.RngSeed,
					(live.Start.ScreenshotUrls ?? new()).ToDictionary(entry => ParseID(entry.Key), entry => entry.Value));
			}

			// the last outcome stays readable, the check itself is over on this copy
			lobby.LastFullMeshConnectivityCheckOutcome = live.Mesh?.LastOutcome;
			lobby.LastFullMeshCheckFailureReason = live.Mesh?.LastFailureReason;
			lobby.FullMeshCheckID = 0;
			lobby.FullMeshCheckAttempt = 0;

			if (lobby.State == ELobbyState.COUNTDOWN)
			{
				if (live.StartPhase == "countdown" && live.CountdownEndsAtMs is long endsAt)
				{
					long remaining = Math.Max(0, endsAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
					lobby.m_StartPhase = EStartPhase.Countdown;
					lobby.m_TimeCountdownEnds = Environment.TickCount64 + remaining;
					lobby.m_RestoredCountdown = true;
				}
				else
				{
					lobby.State = ELobbyState.GAME_SETUP;
					lobby.LastFullMeshConnectivityCheckOutcome = false;
					lobby.LastFullMeshCheckFailureReason = "failover";
				}
			}

			// probes start over, a probe sent by the earlier copy is never expected here
			if (lobby.State == ELobbyState.INGAME && live.ProbesEnabled)
			{
				lobby.CalculateNextProbeTime(true);
			}

			lobby.DirtyRetransmitLobbyList();
			return lobby;
		}

		// The human members whose session has not come back to this copy
		internal IReadOnlyList<Int64> MembersWithoutSession() => Members
			.Where(member => member.IsHuman() && !member.HasLiveSession())
			.Select(member => member.UserID)
			.ToArray();

		// A reconnecting member of a restored lobby takes its seat with the new session, false when the seat is gone or already taken
		internal Task<bool> AttachRestoredMemberAsync(UserSession session) => RunExclusiveAsync(() =>
		{
			LobbyMember? member = GetMemberFromUserID(session.m_UserID);
			if (member == null || member.HasLiveSession() || State == ELobbyState.COMPLETE)
			{
				return Task.FromResult(false);
			}

			member.AttachSession(session);
			session.UpdateSessionLobbyID(LobbyID);
			if (State == ELobbyState.INGAME)
			{
				session.RegisterHistoricMatchID(MatchID, member.SlotIndex, member.Side, TimeCreated);
			}
			DirtyRetransmit();
			return Task.FromResult(true);
		});

		// Quiet for a handover: setup with no countdown, mesh check or start in progress, or in game, under the lobby gate
		private bool IsQuietLocked() => State == ELobbyState.INGAME
			|| (State == ELobbyState.GAME_SETUP && LobbyType == ELobbyType.CustomGame && m_StartPhase == EStartPhase.None && !PendingFullMeshConnectivityChecks);

		// The final state for a handover when the lobby is quiet or forced, null otherwise, from then on the tick writes nothing of it
		internal Task<LobbyLive?> BeginMoveAsync(bool force) => RunExclusiveAsync(() =>
		{
			if (IsMovingAway || State == ELobbyState.COMPLETE || Volatile.Read(ref m_LiveRemoved) != 0 || Volatile.Read(ref m_StartReservations) > 0 || (!force && !IsQuietLocked()))
			{
				return Task.FromResult<LobbyLive?>(null);
			}
			Volatile.Write(ref m_MovingAway, 1);
			return Task.FromResult<LobbyLive?>(CaptureLiveLocked());
		});

		// The handover did not take the lobby, this copy keeps it and writes it again
		internal void CancelMove()
		{
			Volatile.Write(ref m_MovingAway, 0);
			MarkLiveDirty();
		}

		// The sessions of the human members on this copy, connected or inside their reconnect grace
		internal IReadOnlyCollection<UserSession> MemberSessions() => Members
			.Where(member => member.IsHuman())
			.Select(member => member.GetSession().TryGetTarget(out UserSession? session) ? session : null)
			.Where(session => session != null && !session.IsRetired)
			.Cast<UserSession>()
			.ToArray();

		// Seats a session of this copy in a lobby that is not live yet, which replaces a stale local copy of it
		internal void SeatLocked(UserSession session)
		{
			LobbyMember? member = GetMemberFromUserID(session.m_UserID);
			if (member != null && !member.HasLiveSession())
			{
				member.AttachSession(session);
				session.UpdateSessionLobbyID(LobbyID);
			}
		}

		// Frees the seat of a member that a newer restored lobby also holds, before the lobby is live
		internal void RemoveRestoredSeat(Int64 userID)
		{
			LobbyMember? member = GetMemberFromUserID(userID);
			if (member != null && !member.HasLiveSession())
			{
				RemoveMemberLocked(member);
			}
		}

		// Removes a restored member who never reconnected, as the end of a reconnect grace does
		internal async Task<bool> DropRestoredMemberAsync(Int64 userID)
		{
			LobbyMember? removed = await RunExclusiveAsync(() =>
			{
				LobbyMember? member = GetMemberFromUserID(userID);
				if (member == null || member.HasLiveSession())
				{
					return Task.FromResult<LobbyMember?>(null);
				}
				if (State == ELobbyState.INGAME)
				{
					RecordPlayerIngameAbandon(userID);
				}
				return Task.FromResult(RemoveMemberLocked(member) ? member : null);
			});
			if (removed != null)
			{
				PublishMemberRemoval(removed);
			}
			return removed != null;
		}
	}

	public partial class LobbyMember
	{
		// The stored ready flag, IsReady also answers true for the host and AI slots
		internal bool IsReadyStored => m_Ready;

		internal bool HasLiveSession() => PlayerSession.TryGetTarget(out UserSession? session) && session != null && !session.IsRetired;

		internal void AttachSession(UserSession session) => PlayerSession = new WeakReference<UserSession?>(session);

		internal static LobbyMember FromLive(Lobby lobby, LobbyMemberLive slot)
		{
			LobbyMember member = new LobbyMember(lobby, null, slot.UserId, slot.DisplayName ?? String.Empty, slot.NameNotDeduped ?? String.Empty, slot.Port, slot.Side, slot.Color,
				slot.StartPosition, (EPlayerType)slot.SlotState, (UInt16)slot.SlotIndex, slot.HasMap, slot.NameTag);
			member.Team = slot.Team;
			member.m_Ready = slot.Ready;
			member.Region = slot.Region ?? "Unknown";
			return member;
		}
	}
}
