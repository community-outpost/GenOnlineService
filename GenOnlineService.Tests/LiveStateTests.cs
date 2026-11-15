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
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// Build step 3 of docs/realtime-failover.md: the versioned lobby and queue state, the writer, the standby's mirror and the takeover
public class LiveStateTests
{
	// The Redis layout in memory: the state hash, the owner key and a stream that trims its oldest entries
	internal sealed class FakeLiveStore : ILiveStateStore
	{
		private readonly object m_lock = new();
		public readonly Dictionary<string, string> State = new(StringComparer.Ordinal);
		public readonly List<LiveStreamEntry> Stream = new();
		public string? OwnerCopy;
		public long OwnerEpoch;
		public bool Down;
		public int FailNextWrites;
		private long m_nextID;

		public Task WriteAsync(string copy, long epoch, IReadOnlyList<LiveWrite> writes)
		{
			lock (m_lock)
			{
				if (Down || FailNextWrites > 0)
				{
					--FailNextWrites;
					throw new InvalidOperationException("redis down");
				}
				foreach (LiveWrite write in writes)
				{
					switch (write.Op)
					{
						case LiveOp.Reset:
							State.Clear();
							OwnerCopy = copy;
							OwnerEpoch = epoch;
							break;
						case LiveOp.Delete:
							State.Remove(write.Key);
							break;
						case LiveOp.Set:
							State[write.Key] = write.Body!;
							break;
					}
					Stream.Add(new LiveStreamEntry($"{++m_nextID}-0", write.Op, write.Key, copy, epoch, write.Seq, write.Version, write.Body));
				}
			}
			return Task.CompletedTask;
		}

		public Task<LiveSnapshot> LoadAsync()
		{
			lock (m_lock)
			{
				if (Down)
				{
					throw new InvalidOperationException("redis down");
				}
				return Task.FromResult(new LiveSnapshot(new Dictionary<string, string>(State), Stream.LastOrDefault(), OwnerCopy, OwnerEpoch));
			}
		}

		public Task<IReadOnlyList<LiveStreamEntry>> ReadAfterAsync(string position, int count)
		{
			lock (m_lock)
			{
				if (Down)
				{
					throw new InvalidOperationException("redis down");
				}
				long after = long.Parse(position.Split('-')[0]);
				return Task.FromResult<IReadOnlyList<LiveStreamEntry>>(Stream.Where(e => long.Parse(e.ID.Split('-')[0]) > after).Take(count).ToList());
			}
		}

		// XADD MAXLEN, keeps only the newest entries
		public void Trim(int keep)
		{
			lock (m_lock)
			{
				if (Stream.Count > keep)
				{
					Stream.RemoveRange(0, Stream.Count - keep);
				}
			}
		}
	}

	private static string Data(string value) => JsonSerializer.Serialize(new { value }, LiveJson.Options);

	private static string? ValueOf(LiveStateMirror mirror, string key)
	{
		if (!mirror.Entities.TryGetValue(key, out string? json))
		{
			return null;
		}
		LiveEnvelope envelope = LiveJson.ReadEnvelope(json)!;
		return envelope.Data.GetProperty("value").GetString();
	}

	private static LobbyMemberLive Open(int slot) => new() { SlotIndex = slot, SlotState = (int)EPlayerType.SLOT_OPEN };

	private static LobbyLive SetupLobby(long id = 5001) => new()
	{
		Id = id,
		Type = (int)ELobbyType.CustomGame,
		State = (int)ELobbyState.GAME_SETUP,
		Owner = 11,
		Name = "failover",
		MapName = "Tournament Desert",
		MapPath = "maps/tournament desert/tournament desert",
		MapOfficial = true,
		CreatedAt = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc),
		StartingCash = 10000,
		TrackStats = true,
		Passworded = true,
		Password = "secret",
		Build = new LiveBuild { Game = "genonline_zerohour", ExeCrc = 1234567890, IniCrc = 987654321, AnticheatId = -1 },
		NetworkRoomId = 2,
		RngSeed = 77,
		Region = "eu",
		Latitude = 52.5,
		Longitude = 13.4,
		MaxCameraHeight = 310,
		Joinability = (int)ELobbyJoinability.FriendsOnly,
		Slots = new List<LobbyMemberLive>
		{
			new() { SlotIndex = 0, SlotState = (int)EPlayerType.SLOT_PLAYER, UserId = 11, DisplayName = "bob", NameNotDeduped = "bob", NameTag = 1001, Ready = true, Port = 8088, Side = 2, Team = 0, Color = 1, StartPosition = 0, HasMap = true, Region = "Europe" },
			new() { SlotIndex = 1, SlotState = (int)EPlayerType.SLOT_PLAYER, UserId = 12, DisplayName = "carol", NameNotDeduped = "carol", NameTag = 1002, Ready = false, Port = 8089, Side = 3, Team = 1, Color = 2, StartPosition = 1, HasMap = true, Region = "Europe" },
			new() { SlotIndex = 2, SlotState = (int)EPlayerType.SLOT_MED_AI, Side = 4, Color = 3, Region = "Unknown" },
			Open(3),
			new() { SlotIndex = 4, SlotState = (int)EPlayerType.SLOT_CLOSED },
			new() { SlotIndex = 5, SlotState = (int)EPlayerType.SLOT_CLOSED },
			new() { SlotIndex = 6, SlotState = (int)EPlayerType.SLOT_CLOSED },
			new() { SlotIndex = 7, SlotState = (int)EPlayerType.SLOT_CLOSED }
		},
		EverMembers = new List<long> { 11, 12, 13 },
		MemberLeftAt = new Dictionary<string, DateTime> { ["11"] = DateTime.UnixEpoch, ["12"] = DateTime.UnixEpoch, ["13"] = new DateTime(2026, 11, 15, 10, 1, 0, DateTimeKind.Utc) },
		ReportedConnections = new List<long[]> { new long[] { 11, 12 }, new long[] { 12, 11 } },
		PeerPairs = new List<PeerPairLive> { new() { Low = 11, High = 12, Attempt = 3, Step = 2 } },
		Mesh = new LobbyMeshLive { LastOutcome = true }
	};

	private static string Json(LobbyLive live) => LiveJson.Data(live);

	// The same lobby with other users in the two player slots
	private static LobbyLive WithUsers(LobbyLive live, long host, long guest) => live with
	{
		Owner = host,
		Slots = live.Slots.Select(slot => slot.SlotIndex == 0 ? slot with { UserId = host } : slot.SlotIndex == 1 ? slot with { UserId = guest } : slot).ToList(),
		EverMembers = new List<long> { host, guest },
		MemberLeftAt = new Dictionary<string, DateTime>(),
		ReportedConnections = new List<long[]>(),
		PeerPairs = new List<PeerPairLive>()
	};

	private static string EnvelopeAt(LobbyLive live, long writtenAtMs) => JsonSerializer.Serialize(new LiveEnvelope
	{
		V = 1,
		Key = LiveKeys.Lobby(live.Id),
		Owner = "a",
		Epoch = 1,
		Version = 1,
		WrittenAtMs = writtenAtMs,
		Data = JsonSerializer.SerializeToElement(live, LiveJson.Options)
	}, LiveJson.Options);

	private static Task<LobbyLive> Capture(Lobby lobby) => lobby.RunExclusiveAsync(() => Task.FromResult(lobby.CaptureLiveLocked()));

	[Fact]
	public async Task ALobbyInSetupRoundTripsWithFormerMembersReportedPairsAndConnectionOrder()
	{
		LobbyLive stored = SetupLobby();
		Lobby restored = Lobby.FromLive(stored);

		Assert.Equal(Json(stored), Json(await Capture(restored)));
		Assert.True(restored.WasEverMember(13));
		Assert.Equal(new[] { 11L, 12L }, restored.MembersWithoutSession());
		Assert.Equal(ELobbyState.GAME_SETUP, restored.State);
		Assert.Equal("secret", restored.Password);
	}

	[Fact]
	public async Task ALobbyInGameRoundTripsWithItsMatchStartDataAndCounts()
	{
		LobbyLive stored = SetupLobby() with
		{
			State = (int)ELobbyState.INGAME,
			MatchId = 424242,
			Start = new LobbyStartLive { MatchId = 424242, RngSeed = 77, ScreenshotUrls = new Dictionary<string, string?> { ["11"] = "https://s3/one", ["12"] = null } },
			AtStart = new LobbyCountsLive { Humans = 2, Ai = 0, Open = 1, Closed = 4 },
			AbandonedInGame = new Dictionary<string, DateTime> { ["12"] = new DateTime(2026, 11, 15, 10, 5, 0, DateTimeKind.Utc) },
			ReportedOutcomes = new Dictionary<string, bool> { ["11"] = true },
			ProbesEnabled = true
		};
		Lobby restored = Lobby.FromLive(stored);

		Assert.Equal(Json(stored), Json(await Capture(restored)));
		Assert.Equal(424242UL, restored.MatchID);
		Assert.Equal(424242L, restored.StartFor(12)!.MatchId);
		Assert.True(restored.WasPVPAtStart());
	}

	[Fact]
	public async Task ACountdownContinuesFromItsStoredDeadlineWithoutTheMeshWatch()
	{
		long endsAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000;
		LobbyLive stored = SetupLobby() with { State = (int)ELobbyState.COUNTDOWN, StartPhase = "countdown", CountdownEndsAtMs = endsAt };
		Lobby restored = Lobby.FromLive(stored);

		LobbyLive captured = await Capture(restored);
		Assert.Equal((ELobbyState.COUNTDOWN, "countdown"), ((ELobbyState)captured.State, captured.StartPhase));
		Assert.InRange(captured.CountdownEndsAtMs!.Value, endsAt - 250, endsAt + 250);

		// no member is back yet, a countdown with a live mesh check would cancel here
		await restored.ProcessStartCountdown();
		Assert.Equal(ELobbyState.COUNTDOWN, restored.State);
	}

	[Fact]
	public void AStartStillInItsMeshCheckGoesBackToSetup()
	{
		Lobby restored = Lobby.FromLive(SetupLobby() with { State = (int)ELobbyState.COUNTDOWN, StartPhase = "mesh_check" });
		Assert.Equal(ELobbyState.GAME_SETUP, restored.State);
		Assert.False(restored.LastFullMeshConnectivityCheckOutcome);
	}

	[Fact]
	public async Task ARestoredLobbyHasFreshCountersSoAStaleMeshAnswerNeverCounts()
	{
		LobbyLive stored = SetupLobby() with { Mesh = new LobbyMeshLive { CheckId = 3, Attempt = 2, Pending = true, LastOutcome = true } };
		Lobby restored = Lobby.FromLive(stored);

		Assert.Equal((0L, 0, false), (restored.FullMeshCheckID, restored.FullMeshCheckAttempt, restored.PendingFullMeshConnectivityChecks));
		Assert.False(await restored.GetValidatedMeshCheckResultAsync(3));

		// the process counters start at a time based value, far above any id a restarted copy handed out from zero
		long start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 - 3_600_000_000;
		foreach (string name in new[] { "s_NextFullMeshCheckID", "s_NextProbeID" })
		{
			long counter = (long)typeof(Lobby).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
			Assert.True(counter > start, name);
		}
	}

	[Theory]
	[InlineData((int)ELobbyType.CustomGame, (int)ELobbyState.COMPLETE, "finished")]
	[InlineData((int)ELobbyType.QuickMatch, (int)ELobbyState.GAME_SETUP, "quick_match_setup")]
	[InlineData((int)ELobbyType.QuickMatch, (int)ELobbyState.COUNTDOWN, "quick_match_setup")]
	[InlineData((int)ELobbyType.QuickMatch, (int)ELobbyState.INGAME, null)]
	[InlineData((int)ELobbyType.CustomGame, (int)ELobbyState.COUNTDOWN, null)]
	public void FinishedLobbiesAndQuickMatchSetupsAreNotTakenOver(int type, int state, string? reason)
	{
		Assert.Equal(reason, Lobby.SkipReasonOf(SetupLobby() with { Type = type, State = state }));
	}

	[Fact]
	public void UnknownFieldsAreIgnoredAndAMissingVersionIsUnreadable()
	{
		string json = "{\"v\":2,\"key\":\"lobby:5\",\"owner\":\"a\",\"epoch\":3,\"version\":9,\"later_field\":[1,2],\"data\":{\"id\":5,\"name\":\"x\",\"new_thing\":{\"a\":1}}}";
		LiveEnvelope envelope = LiveJson.ReadEnvelope(json)!;
		Assert.Equal((2, 3L, 9L), (envelope.V, envelope.Epoch, envelope.Version));
		Assert.Equal((5L, "x"), (LiveJson.ReadData<LobbyLive>(envelope)!.Id, LiveJson.ReadData<LobbyLive>(envelope)!.Name));

		Assert.Null(LiveJson.ReadEnvelope("{\"key\":\"lobby:5\",\"data\":{}}"));
		Assert.Null(LiveJson.ReadEnvelope("{\"v\":0,\"key\":\"lobby:5\",\"data\":{}}"));
		Assert.Null(LiveJson.ReadEnvelope("not json"));
	}

	[Fact]
	public void TheQueueRoundTripsWithTicketsWideningAndMatchedSetups()
	{
		QueueLive queue = new()
		{
			Tickets = new List<QueueTicketLive>
			{
				new() { UserId = 21, PlaylistId = 0, MapIndexes = new List<int> { 0, 3 }, Build = new LiveBuild { Game = "genonline_zerohour", ExeCrc = 1, IniCrc = 2 }, QueuedAtMs = 1_700_000_000_000, Bucket = 0 },
				new() { UserId = 22, PlaylistId = 0, MapIndexes = new List<int> { 1 }, QueuedAtMs = 1_700_000_001_000, Bucket = -1 },
				new() { UserId = 23, PlaylistId = 0, MapIndexes = new List<int> { 0 }, QueuedAtMs = 1_700_000_002_000, Bucket = 1 }
			},
			Buckets = new List<QueueBucketLive>
			{
				new() { Index = 0, PlaylistId = 0, MapIndexes = new List<int> { 0, 3 }, EloExpansionIteration = 4, Members = new List<long> { 21 } },
				new() { Index = 1, PlaylistId = 0, Phase = "joining", LobbyId = 9001, Members = new List<long> { 23 } }
			}
		};
		string json = LiveJson.Data(queue);
		string envelope = LiveJson.Envelope(LiveKeys.Queue, "a", 1, 1, json);
		QueueLive read = LiveJson.ReadData<QueueLive>(LiveJson.ReadEnvelope(envelope)!)!;
		Assert.Equal(json, LiveJson.Data(read));
	}

	[Fact]
	public void ARestoredQueueKeepsEveryTicketExceptACommittedStartAndAnUnknownPlaylist()
	{
		try
		{
			QueueLive queue = new()
			{
				Tickets = new List<QueueTicketLive>
				{
					new() { UserId = 31, PlaylistId = 0, Bucket = 0 },
					new() { UserId = 32, PlaylistId = 0, Bucket = 1 },
					new() { UserId = 33, PlaylistId = 0, Bucket = -1 },
					new() { UserId = 34, PlaylistId = 60000, Bucket = -1 }
				},
				Buckets = new List<QueueBucketLive>
				{
					new() { Index = 0, PlaylistId = 0, Phase = "mesh_check" },
					new() { Index = 1, PlaylistId = 0, Phase = "committed" }
				}
			};
			Assert.Equal(2, MatchmakingManager.RestoreQueue(queue, 60_000));
			Assert.True(MatchmakingManager.HasRestoredTickets);
		}
		finally
		{
			MatchmakingManager.RestoreQueue(new QueueLive(), 0);
		}
		Assert.False(MatchmakingManager.HasRestoredTickets);
	}

	private static LobbyManager Lobbies() => new LobbyManager(null!, null!, null!, null!, NullLogger<LobbyManager>.Instance);

	[Fact]
	public void TakeoverRestoresEachReadableLobbyAndCountsTheRest()
	{
		LobbyManager lobbies = Lobbies();
		Dictionary<string, string> mirror = new()
		{
			[LiveKeys.Lobby(1)] = LiveJson.Envelope(LiveKeys.Lobby(1), "a", 1, 1, Json(SetupLobby(1))),
			[LiveKeys.Lobby(2)] = LiveJson.Envelope(LiveKeys.Lobby(2), "a", 1, 1, Json(WithUsers(SetupLobby(2), 21, 22) with { State = (int)ELobbyState.INGAME, MatchId = 5 })),
			[LiveKeys.Lobby(3)] = "{broken",
			[LiveKeys.Lobby(4)] = LiveJson.Envelope(LiveKeys.Lobby(4), "a", 1, 1, Json(SetupLobby(4) with { Slots = new List<LobbyMemberLive> { Open(0) } })),
			[LiveKeys.Lobby(5)] = LiveJson.Envelope(LiveKeys.Lobby(5), "a", 1, 1, Json(SetupLobby(5) with { Type = (int)ELobbyType.QuickMatch }))
		};

		LiveRestoreResult result = LiveStateTakeover.Restore(mirror, lobbies, 30_000);

		Assert.Equal(new LiveRestoreResult(2, 2, 1, 0), result);
		Assert.Equal(ELobbyState.GAME_SETUP, lobbies.GetLobby(1)!.State);
		Assert.Equal(5UL, lobbies.GetLobby(2)!.MatchID);
		Assert.Null(lobbies.GetLobby(3));
		Assert.Null(lobbies.GetLobby(5));
		Assert.True(lobbies.HasRestoredMembers);
	}

	[Fact]
	public async Task AReconnectingMemberTakesItsSeatInTheRestoredLobbyOnce()
	{
		Lobby restored = Lobby.FromLive(SetupLobby() with { State = (int)ELobbyState.INGAME, MatchId = 77 });
		UserSession carol = new UserSession(12, EUserSessionType.GameClient, "", "", "", 0, 0);

		Assert.True(await restored.AttachRestoredMemberAsync(carol));
		Assert.Equal(restored.LobbyID, carol.currentLobbyID);
		Assert.True(restored.GetMemberFromUserID(12)!.GetSession().TryGetTarget(out UserSession? seated) && ReferenceEquals(seated, carol));
		Assert.Equal(new[] { 11L }, restored.MembersWithoutSession());

		// the seat is taken, a second session never replaces it
		Assert.False(await restored.AttachRestoredMemberAsync(new UserSession(12, EUserSessionType.GameClient, "", "", "", 0, 0)));
		Assert.False(await restored.AttachRestoredMemberAsync(new UserSession(99, EUserSessionType.GameClient, "", "", "", 0, 0)));
	}

	[Fact]
	public async Task ARestoredMemberWhoNeverReconnectsLeavesAfterTheGrace()
	{
		Lobby restored = Lobby.FromLive(SetupLobby());
		Assert.True(await restored.DropRestoredMemberAsync(12));
		Assert.Null(restored.GetMemberFromUserID(12));

		// a member who reconnected keeps the seat
		UserSession bob = new UserSession(11, EUserSessionType.GameClient, "", "", "", 0, 0);
		await restored.AttachRestoredMemberAsync(bob);
		Assert.False(await restored.DropRestoredMemberAsync(11));
		Assert.NotNull(restored.GetMemberFromUserID(11));
	}

	[Fact]
	public async Task TheMirrorAppliesTheStreamInOrder()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = new(store, "a", () => 1);
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);

		writer.Set("lobby:1", Data("one"));
		writer.Set("lobby:2", Data("two"));
		await writer.FlushAsync();
		await follower.StepAsync();
		Assert.Equal(("one", "two"), (ValueOf(follower.Mirror, "lobby:1"), ValueOf(follower.Mirror, "lobby:2")));

		writer.Set("lobby:1", Data("one again"));
		writer.Delete("lobby:2");
		writer.Set("queue", Data("q"));
		await writer.FlushAsync();
		Assert.Equal(3, await follower.StepAsync());
		Assert.Equal(("one again", null, "q"), (ValueOf(follower.Mirror, "lobby:1"), ValueOf(follower.Mirror, "lobby:2"), ValueOf(follower.Mirror, "queue")));

		// the stream entries carry a sequence per writer and a version per entity
		LiveStreamEntry last = store.Stream.Last();
		Assert.Equal(("a", 1L, 6L, 1L), (last.Copy, last.Epoch, last.Seq, last.Version));
		Assert.Equal(2L, LiveJson.ReadEnvelope(store.State["lobby:1"])!.Version);
	}

	[Fact]
	public async Task AStreamTrimmedPastTheFollowerTriggersAFullReload()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = new(store, "a", () => 1);
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		writer.Set("lobby:1", Data("one"));
		await writer.FlushAsync();
		await follower.StepAsync();

		for (int i = 0; i < 5; i++)
		{
			writer.Set("lobby:" + (10 + i), Data("n" + i));
			await writer.FlushAsync();
		}
		writer.Delete("lobby:1");
		await writer.FlushAsync();
		store.Trim(2);

		await follower.StepAsync();
		Assert.False(follower.Mirror.Loaded);
		Assert.False(follower.InSync);

		await follower.StepAsync();
		Assert.True(follower.InSync);
		Assert.Null(ValueOf(follower.Mirror, "lobby:1"));
		Assert.Equal(new[] { "lobby:10", "lobby:11", "lobby:12", "lobby:13", "lobby:14" }, follower.Mirror.Entities.Keys.OrderBy(k => k).ToArray());
	}

	[Fact]
	public async Task ANewOwnerStartsWithAResetAndALateWriteOfTheOldEpochIsIgnored()
	{
		FakeLiveStore store = new();
		LiveStateWriter a = new(store, "a", () => 1);
		LiveStateFollower follower = new(store, "c", () => true, 100, 2500);
		a.Set("lobby:1", Data("from a"));
		await a.FlushAsync();
		await follower.StepAsync();

		LiveStateWriter b = new(store, "b", () => 2);
		b.RequestReset();
		await b.FlushAsync();
		b.Set("lobby:2", Data("from b"));
		await b.FlushAsync();

		// a fenced copy that wrote once more after the takeover
		a.Set("lobby:3", Data("late"));
		await a.FlushAsync();

		await follower.StepAsync();
		Assert.True(follower.Mirror.Loaded);
		Assert.Equal(("b", 2L), (follower.Mirror.WriterCopy, follower.Mirror.WriterEpoch));
		Assert.Equal(new[] { "lobby:2" }, follower.Mirror.Entities.Keys.ToArray());

		// a standby loading now keeps the hash entities of the owner epoch only
		LiveStateFollower fresh = new(store, "d", () => true, 100, 2500);
		await fresh.StepAsync();
		Assert.Equal(new[] { "lobby:2" }, fresh.Mirror.Entities.Keys.ToArray());
	}

	[Fact]
	public async Task AnEmptyNewOwnerStillClearsTheStateOfTheOldOne()
	{
		FakeLiveStore store = new();
		LiveStateWriter a = new(store, "a", () => 1);
		a.Set("lobby:1", Data("from a"));
		await a.FlushAsync();

		long? epoch = null;
		LiveStateWriter b = new(store, "b", () => epoch);
		b.RequestReset();
		Assert.Equal(0, await b.FlushAsync());

		// the reset waits until the copy owns the state
		epoch = 2;
		Assert.Equal(1, await b.FlushAsync());
		Assert.Empty(store.State);
		Assert.Equal(("b", 2L), (store.OwnerCopy, store.OwnerEpoch));
	}

	[Fact]
	public async Task ACopyThatDoesNotOwnTheStateWritesNothingAndWritesEverythingAgainLater()
	{
		FakeLiveStore store = new();
		long? epoch = null;
		LiveStateWriter writer = new(store, "a", () => epoch);
		int before = writer.Generation;

		writer.Set("lobby:1", Data("x"));
		await writer.FlushAsync();
		Assert.Empty(store.Stream);
		Assert.False(writer.Writing);
		Assert.NotEqual(before, writer.Generation);

		epoch = 1;
		int generation = writer.Generation;
		writer.Set("lobby:1", Data("x"));
		await writer.FlushAsync();
		Assert.Equal(new[] { LiveOp.Reset, LiveOp.Set }, store.Stream.Select(e => e.Op).ToArray());
		Assert.NotEqual(generation, writer.Generation);
	}

	[Fact]
	public async Task AFailedBatchMakesTheFollowerReloadAndEveryEntityIsWrittenAgain()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = new(store, "a", () => 1);
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		writer.Set("lobby:1", Data("one"));
		await writer.FlushAsync();
		await follower.StepAsync();

		int generation = writer.Generation;
		store.Down = true;
		writer.Set("lobby:1", Data("lost"));
		await writer.FlushAsync();
		Assert.NotEqual(generation, writer.Generation);

		store.Down = false;
		writer.Set("lobby:1", Data("again"));
		await writer.FlushAsync();
		await follower.StepAsync();
		Assert.False(follower.Mirror.Loaded);
		await follower.StepAsync();
		Assert.Equal("again", ValueOf(follower.Mirror, "lobby:1"));
	}

	[Fact]
	public async Task TheFollowerIsInSyncOnlyWhenCaughtUpAndRecentlyRead()
	{
		FakeLiveStore store = new();
		long now = 1_000;
		bool follow = true;
		LiveStateFollower follower = new(store, "b", () => follow, 100, 2500, () => now);
		Assert.False(follower.InSync);

		await follower.StepAsync();
		Assert.True(follower.InSync);

		// a full batch means more entries wait
		LiveStateWriter writer = new(store, "a", () => 1);
		for (int i = 0; i < LiveStateFollower.BatchSize + 10; i++)
		{
			writer.Set("lobby:" + i, Data("x"));
		}
		await writer.FlushAsync();
		Assert.Equal(LiveStateFollower.BatchSize, await follower.StepAsync());
		Assert.False(follower.InSync);
		await follower.StepAsync();
		Assert.True(follower.InSync);

		// no successful read for longer than a lease
		now += 2600;
		Assert.False(follower.InSync);
		now += 10;
		await follower.StepAsync();
		Assert.True(follower.InSync);

		store.Down = true;
		await follower.StepAsync();
		Assert.False(follower.InSync);
		store.Down = false;

		// the active copy does not follow
		await follower.StepAsync();
		Assert.True(follower.InSync);
		follow = false;
		await follower.StepAsync();
		Assert.False(follower.InSync);
		Assert.False(follower.Mirror.Loaded);
	}

	[Fact]
	public async Task DetachingTheMirrorHandsOverEveryEntityAndStopsTheSync()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = new(store, "a", () => 1);
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		writer.Set("lobby:1", Data("one"));
		writer.Set("queue", Data("q"));
		await writer.FlushAsync();
		await follower.StepAsync();

		IReadOnlyDictionary<string, string> mirror = follower.Detach().Entities;
		Assert.Equal(new[] { "lobby:1", "queue" }, mirror.Keys.OrderBy(k => k).ToArray());
		Assert.False(follower.InSync);
		Assert.False(follower.Mirror.Loaded);
	}

	[Fact]
	public void WithoutLiveStateEveryCallIsANoOpAndTheStandbyCountsAsInSync()
	{
		Assert.False(RealtimeLiveState.Enabled);
		Assert.False(RealtimeLiveState.Writing);
		Assert.True(RealtimeLiveState.StandbyInSync);
		RealtimeLiveState.Set("lobby:1", "{}");
		RealtimeLiveState.Delete("lobby:1");
		Assert.Empty(RealtimeLiveState.DetachMirror().Entities);
		Assert.False(RealtimeLiveState.DetachMirror().Fresh);
	}

	[Fact]
	public async Task ADeleteLostInAFailedBatchIsClearedByTheResetOfTheNextBatch()
	{
		FakeLiveStore store = new();
		LiveStateWriter writer = new(store, "a", () => 1);
		writer.Set("lobby:1", Data("one"));
		writer.Set("lobby:2", Data("two"));
		await writer.FlushAsync();

		store.FailNextWrites = 1;
		writer.Delete("lobby:1");
		await writer.FlushAsync();
		Assert.True(store.State.ContainsKey("lobby:1"));

		// the next batch starts with a reset, the live lobby is written again after it
		writer.Set("lobby:2", Data("two"));
		await writer.FlushAsync();
		Assert.False(store.State.ContainsKey("lobby:1"));
		Assert.Equal(new[] { LiveOp.Reset, LiveOp.Set }, store.Stream.TakeLast(2).Select(e => e.Op).ToArray());

		// a standby loading now never sees the deleted lobby
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		await follower.StepAsync();
		Assert.Equal(new[] { "lobby:2" }, follower.Mirror.Entities.Keys.ToArray());
	}

	[Fact]
	public void AUserStoredInTwoLobbiesKeepsTheSeatOfTheNewerOne()
	{
		LobbyManager lobbies = Lobbies();
		LobbyLive older = WithUsers(SetupLobby(41), 51, 52);
		LobbyLive newer = WithUsers(SetupLobby(42), 53, 52);
		LobbyLive alone = WithUsers(SetupLobby(43), 54, 55) with
		{
			Slots = WithUsers(SetupLobby(43), 54, 55).Slots.Select(slot => slot.SlotIndex == 0 ? slot with { UserId = 53 } : slot.SlotIndex == 1 ? Open(1) : slot).ToList(),
			Owner = 53
		};
		Dictionary<string, string> mirror = new()
		{
			[LiveKeys.Lobby(41)] = EnvelopeAt(older, 1_000),
			[LiveKeys.Lobby(42)] = EnvelopeAt(newer, 2_000),
			[LiveKeys.Lobby(43)] = EnvelopeAt(alone, 500)
		};

		LiveRestoreResult result = LiveStateTakeover.Restore(mirror, lobbies, 30_000);

		// carol (52) sits in the newer lobby only, the oldest lobby lost its only member to the newer one and stays out
		Assert.Equal(new LiveRestoreResult(2, 0, 1, 0), result);
		Assert.Null(lobbies.GetLobby(41)!.GetMemberFromUserID(52));
		Assert.NotNull(lobbies.GetLobby(42)!.GetMemberFromUserID(52));
		Assert.NotNull(lobbies.GetLobby(41)!.GetMemberFromUserID(51));
		Assert.Null(lobbies.GetLobby(43));
	}

	[Fact]
	public async Task OnlyAMirrorInSyncWithinTwoLeasePeriodsIsFresh()
	{
		FakeLiveStore store = new();
		long now = 10_000;
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500, () => now);
		Assert.False(follower.Detach().Fresh);

		await follower.StepAsync();
		now += 4_000;
		Assert.True(follower.Detach().Fresh);

		await follower.StepAsync();
		now += 5_001;
		Assert.False(follower.Detach().Fresh);
	}

	[Fact]
	public void UnclaimedRestoredTicketsStayInTheCapturedQueueDuringTheGrace()
	{
		try
		{
			QueueLive queue = new()
			{
				Tickets = new List<QueueTicketLive>
				{
					new() { UserId = 9031, PlaylistId = 0, MapIndexes = new List<int> { 0 }, QueuedAtMs = 1_700_000_000_000, Bucket = 3 },
					new() { UserId = 9032, PlaylistId = 0, MapIndexes = new List<int> { 1 }, QueuedAtMs = 1_700_000_001_000, Bucket = -1 }
				},
				Buckets = new List<QueueBucketLive> { new() { Index = 3, PlaylistId = 0, EloExpansionIteration = 5, Members = new List<long> { 9031 } } }
			};
			MatchmakingManager.RestoreQueue(queue, 60_000);

			QueueLive captured = MatchmakingManager.CaptureQueue();
			QueueTicketLive first = captured.Tickets.Single(t => t.UserId == 9031);
			QueueTicketLive second = captured.Tickets.Single(t => t.UserId == 9032);
			Assert.Equal((1_700_000_000_000L, -1), (first.QueuedAtMs, second.Bucket));
			QueueBucketLive bucket = captured.Buckets.Single(b => b.Index == first.Bucket);
			Assert.Equal(5, bucket.EloExpansionIteration);
			Assert.Equal(new[] { 9031L }, bucket.Members);

			// once the grace is over they are gone
			MatchmakingManager.RestoreQueue(queue, 0);
			Assert.DoesNotContain(MatchmakingManager.CaptureQueue().Tickets, t => t.UserId == 9031);
		}
		finally
		{
			MatchmakingManager.RestoreQueue(new QueueLive(), 0);
		}
	}
}
