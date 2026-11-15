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
using Xunit;

namespace GenOnlineService.Tests;

// The matchmaking statics are shared, so the live state tests run one after the other
[CollectionDefinition(Name)]
public sealed class LiveStateCollection
{
	public const string Name = "live state statics";
}

// Build step 4: the queue and the lobbies move from copy a to copy b, the target takes them from its mirror, a cancel takes them back
[Collection(LiveStateCollection.Name)]
public class LiveHandoverTests
{
	private sealed class RecordingSwitcher : ISessionSwitcher
	{
		private readonly Func<IReadOnlyCollection<UserSession>, bool> m_arrived;
		public readonly List<(long[] Users, bool Arrived, bool Kept, bool KeepRelay)> Switches = new();

		public RecordingSwitcher(Func<IReadOnlyCollection<UserSession>, bool>? arrived = null)
		{
			m_arrived = arrived ?? (_ => true);
		}

		public Task SwitchSessionsAsync(IReadOnlyCollection<UserSession> sessions, bool kept, bool keepRelay = false)
		{
			lock (Switches)
			{
				Switches.Add((sessions.Select(s => s.m_UserID).OrderBy(id => id).ToArray(), m_arrived(sessions), kept, keepRelay));
			}
			return Task.CompletedTask;
		}

		public int Count
		{
			get
			{
				lock (Switches)
				{
					return Switches.Count;
				}
			}
		}
	}

	// One copy: its lobbies, its writer, its follower and its handover
	private sealed class Copy
	{
		public readonly LobbyManager Lobbies = new(null!, null!, null!, null!, NullLogger<LobbyManager>.Instance);
		public readonly LiveStateWriter Writer;
		public readonly LiveStateFollower Follower;
		public readonly LiveStateHandover Handover;
		public readonly RecordingSwitcher Switcher;

		public Copy(FakeLiveStore store, string id, int adoptTimeoutMs = 3000, Func<IReadOnlyCollection<UserSession>, bool>? arrived = null)
		{
			Writer = new LiveStateWriter(store, id, () => true);
			Follower = new LiveStateFollower(store, id, () => true, 10, 2500);
			Switcher = new RecordingSwitcher(arrived);
			Handover = new LiveStateHandover(Writer, store, Lobbies, Switcher, adoptTimeoutMs);
			Writer.Rejected += Handover.OnRejected;
			Follower.Owned += Handover.OnOwned;
		}

		// The writer loop alone, as on a copy that holds the lease and follows no stream
		public Task PumpWriterAsync(CancellationToken stop) => Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				await Writer.FlushAsync();
				await Task.Delay(5);
			}
		});

		// The loops of a running copy, until the token stops them
		public Task PumpAsync(CancellationToken stop) => Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				await Follower.StepAsync();
				await Writer.FlushAsync();
				await Task.Delay(5);
			}
		});
	}

	private static LobbyMemberLive Slot(int index, long user) => user > 0
		? new() { SlotIndex = index, SlotState = (int)EPlayerType.SLOT_PLAYER, UserId = user, DisplayName = "u" + user, NameNotDeduped = "u" + user, Port = 8088 }
		: new() { SlotIndex = index, SlotState = (int)(index < 4 ? EPlayerType.SLOT_OPEN : EPlayerType.SLOT_CLOSED) };

	private static LobbyLive Stored(long id, ELobbyState state, params long[] users) => new()
	{
		Id = id,
		Type = (int)ELobbyType.CustomGame,
		State = (int)state,
		Owner = users[0],
		Name = "lobby " + id,
		CreatedAt = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc),
		Build = new LiveBuild { Game = "genonline_zerohour" },
		MatchId = state == ELobbyState.INGAME ? (ulong)(9000 + id) : 0,
		StartPhase = state == ELobbyState.COUNTDOWN ? "countdown" : "none",
		CountdownEndsAtMs = state == ELobbyState.COUNTDOWN ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000 : null,
		Slots = Enumerable.Range(0, Lobby.MaxLobbySize).Select(i => Slot(i, i < users.Length ? users[i] : 0)).ToList(),
		EverMembers = users.ToList()
	};

	// A lobby of copy a with a connected session per member, written once so a owns it
	private static async Task<(Lobby Lobby, UserSession[] Sessions)> Live(Copy copy, LobbyLive stored)
	{
		Lobby lobby = Lobby.FromLive(stored);
		Assert.True(copy.Lobbies.AdoptRestoredLobby(lobby, 60_000));
		List<UserSession> sessions = new();
		foreach (long user in stored.EverMembers)
		{
			UserSession session = new(user, EUserSessionType.GameClient, "", "", "", 0, 0);
			Assert.True(await lobby.AttachRestoredMemberAsync(session));
			sessions.Add(session);
		}
		copy.Writer.Set(LiveKeys.Lobby(stored.Id), LiveJson.Data(stored));
		await copy.Writer.FlushAsync();
		return (lobby, sessions.ToArray());
	}

	private static async Task Until(Func<bool> done, int ms = 5000)
	{
		long until = Environment.TickCount64 + ms;
		while (!done() && Environment.TickCount64 < until)
		{
			await Task.Delay(10);
		}
		Assert.True(done());
	}

	[Fact]
	public async Task QuietLobbiesMoveOneByOneWithAllTheirMembersAndABusyOneWaitsForTheLimit()
	{
		FakeLiveStore store = new();
		Copy b = new(store, "b");
		Copy a = new(store, "a", arrived: sessions => sessions.All(s => b.Lobbies.GetLobby(s.currentLobbyID) != null));
		List<string> refused = new();
		a.Writer.Rejected += refused.Add;

		// lobbies hold their sessions weakly, the test keeps them alive as the session registry does
		(_, UserSession[] first) = await Live(a, Stored(1, ELobbyState.GAME_SETUP, 11, 12));
		(_, UserSession[] second) = await Live(a, Stored(2, ELobbyState.INGAME, 21, 22));
		(Lobby busy, UserSession[] third) = await Live(a, Stored(3, ELobbyState.COUNTDOWN, 31));

		using CancellationTokenSource stop = new();
		Task pump = b.PumpAsync(stop.Token);

		// the lobby ticks of copy a keep writing while the moves run
		Task writes = Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				foreach (long id in new long[] { 1, 2, 3 })
				{
					Lobby? lobby = a.Lobbies.GetLobby(id);
					if (lobby != null && !lobby.IsMovingAway)
					{
						a.Writer.Set(LiveKeys.Lobby(id), LiveJson.Data(lobby.CaptureLiveLocked()));
					}
				}
				await a.Writer.FlushAsync();
				await Task.Delay(3);
			}
		});

		long clock = 0;
		Task<bool> moving = a.Handover.MoveAsync("b", 1_000, () => Interlocked.Read(ref clock), () => Task.FromResult(true));
		await Until(() => a.Switcher.Count == 2);
		await Task.Delay(300);
		Assert.Equal(2, a.Switcher.Count);
		Assert.NotNull(a.Lobbies.GetLobby(3));
		Assert.False(busy.IsMovingAway);

		// at the limit the busy lobby moves anyway
		Interlocked.Exchange(ref clock, 1_000);
		Assert.True(await moving);
		stop.Cancel();
		await Task.WhenAll(pump, writes);

		// every lobby arrived on b before its members switched, the members of one lobby switched together
		Assert.Equal(new[] { new long[] { 11, 12 }, new long[] { 21, 22 }, new long[] { 31 } }, a.Switcher.Switches.Select(s => s.Users).OrderBy(u => u[0]).ToArray());
		Assert.All(a.Switcher.Switches, s => Assert.True(s.Arrived));
		Assert.Empty(a.Lobbies.GetLobbies());
		Assert.Empty(refused);

		Assert.Equal((ELobbyState.GAME_SETUP, ELobbyState.INGAME, ELobbyState.COUNTDOWN), (b.Lobbies.GetLobby(1)!.State, b.Lobbies.GetLobby(2)!.State, b.Lobbies.GetLobby(3)!.State));
		Assert.Equal(9002UL, b.Lobbies.GetLobby(2)!.MatchID);
		Assert.Equal(new[] { 11L, 12L }, b.Lobbies.GetLobby(1)!.MembersWithoutSession());
		foreach (long id in new long[] { 1, 2, 3 })
		{
			string key = LiveKeys.Lobby(id);
			Assert.Equal(("b", "b", 2L), (LiveJson.ReadEnvelope(store.State[key])!.Owner, LiveJson.ReadEnvelope(store.State[key])!.WrittenBy, store.Epochs[key].Epoch));
			Assert.False(a.Writer.Owns(key));
			Assert.True(b.Writer.Owns(key));
		}
		GC.KeepAlive((first, second, third));
	}

	[Fact]
	public async Task TheQueueMovesFirstWithTheNextOwnerEpochAndOnlyTheNewOwnerFormsMatches()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		Copy b = new(store, "b");
		Task<IReadOnlyDictionary<string, long>> claim = a.Writer.ClaimAsync(LiveClaim.NotMine, null, new[] { LiveKeys.Queue });
		await a.Writer.FlushAsync();
		await claim;
		(_, UserSession[] sessions) = await Live(a, Stored(4, ELobbyState.GAME_SETUP, 41));

		using CancellationTokenSource stop = new();
		Task pump = b.PumpAsync(stop.Token);
		Task writer = a.PumpWriterAsync(stop.Token);
		try
		{
			Assert.True(await a.Handover.MoveAsync("b", long.MaxValue, () => 0, () => Task.FromResult(true)));
		}
		finally
		{
			stop.Cancel();
			await Task.WhenAll(pump, writer);
			MatchmakingManager.HoldFormation = false;
		}

		// the queued players switch first, an empty group here, then the lobby
		Assert.Equal(new[] { Array.Empty<long>(), new long[] { 41 } }, a.Switcher.Switches.Select(s => s.Users).ToArray());
		Assert.Equal(("b", "b", 2L), (LiveJson.ReadEnvelope(store.State[LiveKeys.Queue])!.Owner, LiveJson.ReadEnvelope(store.State[LiveKeys.Queue])!.WrittenBy, store.Epochs[LiveKeys.Queue].Epoch));
		Assert.False(a.Writer.Owns(LiveKeys.Queue));
		Assert.True(b.Writer.Owns(LiveKeys.Queue));
		GC.KeepAlive(sessions);
	}

	[Fact]
	public async Task ATargetThatNeverTakesTheLobbyStopsTheMoveAndTheTakeBackKeepsItsMembersHere()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a", adoptTimeoutMs: 200);
		(Lobby lobby, UserSession[] sessions) = await Live(a, Stored(5, ELobbyState.GAME_SETUP, 51, 52));

		using (CancellationTokenSource stop = new())
		{
			Task writer = a.PumpWriterAsync(stop.Token);
			Assert.False(await a.Handover.MoveAsync("b", long.MaxValue, () => 0, () => Task.FromResult(true)));
			stop.Cancel();
			await writer;
		}
		Assert.True(lobby.IsMovingAway);
		Assert.Empty(a.Switcher.Switches);

		// the cancel reads what b owns, the lobby that never left, and claims it back with the next owner epoch
		await a.Handover.PrepareTakeBackAsync("b");
		a.Handover.OnActivating(FailoverActivation.TakenBack, 2);
		await a.Writer.FlushAsync();
		Assert.Same(lobby, a.Lobbies.GetLobby(5));
		Assert.False(lobby.IsMovingAway);
		Assert.True(lobby.GetMemberFromUserID(51)!.GetSession().TryGetTarget(out UserSession? seated) && ReferenceEquals(seated, sessions[0]));
		Assert.Equal(3L, a.Writer.EpochOf(LiveKeys.Lobby(5)));
		Assert.Equal((3L, "a"), (store.Epochs[LiveKeys.Lobby(5)].Epoch, store.Epochs[LiveKeys.Lobby(5)].Owner));
	}

	[Fact]
	public async Task ALobbyMovedToATargetThatDiesIsTakenBackFromRedisForItsReconnectingMembers()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		Copy b = new(store, "b");
		(_, UserSession[] sessions) = await Live(a, Stored(6, ELobbyState.INGAME, 61, 62));

		using (CancellationTokenSource stop = new())
		{
			Task pump = b.PumpAsync(stop.Token);
			Task writer = a.PumpWriterAsync(stop.Token);
			Assert.True(await a.Handover.MoveAsync("b", long.MaxValue, () => 0, () => Task.FromResult(true)));
			stop.Cancel();
			await Task.WhenAll(pump, writer);
		}
		Assert.Null(a.Lobbies.GetLobby(6));

		// b is gone, a takes back what b owned: the lobby returns with free seats for the members who reconnect
		await a.Handover.PrepareTakeBackAsync("b");
		a.Handover.OnActivating(FailoverActivation.TakenBack, 2);
		await a.Writer.FlushAsync();
		Lobby back = a.Lobbies.GetLobby(6)!;
		Assert.Equal((ELobbyState.INGAME, 9006UL), (back.State, back.MatchID));
		Assert.Equal(new[] { 61L, 62L }, back.MembersWithoutSession());
		Assert.True(a.Lobbies.HasRestoredMembers);
		Assert.Equal((3L, "a"), (store.Epochs[LiveKeys.Lobby(6)].Epoch, store.Epochs[LiveKeys.Lobby(6)].Owner));

		// a late write of b is refused
		b.Writer.Set(LiveKeys.Lobby(6), LiveJson.Data(Stored(6, ELobbyState.INGAME, 61, 62)));
		await b.Writer.FlushAsync();
		Assert.False(b.Writer.Owns(LiveKeys.Lobby(6)));
		GC.KeepAlive(sessions);
	}

	[Fact]
	public async Task ALobbyWhoseWriteIsRefusedLeavesThisCopyAndItsMembersSwitch()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		(_, UserSession[] sessions) = await Live(a, Stored(7, ELobbyState.GAME_SETUP, 71, 72));

		// another copy took over after a failure
		LiveStateWriter c = new(store, "c", () => true);
		Task<IReadOnlyDictionary<string, long>> claim = c.ClaimAsync(LiveClaim.NotMine, null, Array.Empty<string>());
		await c.FlushAsync();
		await claim;

		a.Writer.Set(LiveKeys.Lobby(7), LiveJson.Data(Stored(7, ELobbyState.GAME_SETUP, 71, 72)));
		await a.Writer.FlushAsync();
		await Until(() => a.Switcher.Count == 1);
		Assert.Null(a.Lobbies.GetLobby(7));
		Assert.Equal(new long[] { 71, 72 }, a.Switcher.Switches[0].Users);

		// the new owner holds the lobby, so its members keep their relay credentials, their presence is published as usual
		Assert.Equal((false, true), (a.Switcher.Switches[0].Kept, a.Switcher.Switches[0].KeepRelay));
		GC.KeepAlive(sessions);
	}

	[Fact]
	public async Task AStaleLocalCopyGivesWayToTheStoredStateAndItsConnectedMembersKeepTheirSeats()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		(Lobby stale, UserSession[] sessions) = await Live(a, Stored(8, ELobbyState.GAME_SETUP, 81, 82));

		Lobby stored = Lobby.FromLive(Stored(8, ELobbyState.INGAME, 81, 82));
		Assert.True(a.Lobbies.AdoptRestoredLobby(stored, 60_000));
		Assert.Same(stored, a.Lobbies.GetLobby(8));
		Assert.Equal(ELobbyState.INGAME, a.Lobbies.GetLobby(8)!.State);
		Assert.Empty(stored.MembersWithoutSession());
		Assert.True(stored.GetMemberFromUserID(82)!.GetSession().TryGetTarget(out UserSession? seated) && ReferenceEquals(seated, sessions[1]));
		GC.KeepAlive((stale, sessions));
	}

	[Fact]
	public async Task AMovingLobbyIsFrozenItsCommandsAreRefusedAndACancelResumesItsCountdown()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		LobbyLive stored = Stored(10, ELobbyState.COUNTDOWN, 101) with { CountdownEndsAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 150 };
		(Lobby lobby, UserSession[] sessions) = await Live(a, stored);
		Assert.Null(GenOnlineService.Realtime.RealtimeHub.MovingRefusal(lobby));

		LobbyLive? captured = await lobby.BeginMoveAsync(force: true);
		Assert.NotNull(captured);
		Assert.Equal(GenOnlineService.Rest.RestCodes.ServiceUnavailable, GenOnlineService.Realtime.RealtimeHub.MovingRefusal(lobby)!.Value.Code);

		// past the deadline the frozen countdown does not start the game here, the target restores the same deadline
		await Task.Delay(250);
		await lobby.Tick();
		Assert.Equal(ELobbyState.COUNTDOWN, lobby.State);
		Assert.InRange(captured!.CountdownEndsAtMs!.Value, stored.CountdownEndsAtMs!.Value - 100, stored.CountdownEndsAtMs!.Value + 100);

		// a cancel unfreezes it and the countdown ends at the same deadline
		lobby.CancelMove();
		Assert.Null(GenOnlineService.Realtime.RealtimeHub.MovingRefusal(lobby));
		await lobby.Tick();
		Assert.Equal(ELobbyState.INGAME, lobby.State);
		GC.KeepAlive(sessions);
	}

	[Fact]
	public async Task APlayerQueuedAfterTheQueueLeftFollowsWithATicketOfItsOwn()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		Copy b = new(store, "b");
		UserSession late = new(9201, EUserSessionType.GameClient, "", "", "", 0, 0);
		late.MatchmakingPlaylistID = 0;
		late.MatchmakingMapIndicies = new ConcurrentList<int>(new[] { 0 });
		late.IsRegisteredForMatchmaking = true;
		Assert.True(await MatchmakingManager.TryRequeueRegisteredPlayer(late, GenOnlineService.Realtime.MatchRequeueReason.SetupFailed));

		using CancellationTokenSource stop = new();
		Task pump = b.PumpAsync(stop.Token);
		Task writer = a.PumpWriterAsync(stop.Token);
		try
		{
			Assert.True(await a.Handover.MoveAsync("b", long.MaxValue, () => 0, () => Task.FromResult(true)));
		}
		finally
		{
			stop.Cancel();
			await Task.WhenAll(pump, writer);
		}

		try
		{
			Assert.Equal(new[] { new long[] { 9201 } }, a.Switcher.Switches.Select(s => s.Users).ToArray());
			Assert.False(late.IsRegisteredForMatchmaking);
			Assert.True(MatchmakingManager.HasRestoredTicket(9201));
			Assert.False(store.State.ContainsKey(LiveKeys.Ticket(9201)));
		}
		finally
		{
			MatchmakingManager.RestoreQueue(new QueueLive(), 0);
		}
	}

	[Fact]
	public async Task AnEntityHandedToThisCopyButNotYetTakenIsRestoredWhenTheOldOwnerDies()
	{
		FakeLiveStore store = new();
		Copy b = new(store, "b");
		Dictionary<string, string> handed = new()
		{
			[LiveKeys.Lobby(11)] = LiveJson.Envelope(LiveKeys.Lobby(11), "b", 2, 1, LiveJson.Data(Stored(11, ELobbyState.INGAME, 111)), "a")
		};
		LiveStateTakeover.OnActivating(FailoverActivation.TakenBack, 3, b.Lobbies, b.Writer, handed, "a");
		Assert.NotNull(b.Lobbies.GetLobby(11));
	}

	[Fact]
	public async Task ASeatOrATicketThatEndsUnclaimedTellsFriendsThePlayerIsOffline()
	{
		List<long> published = new();
		Action<long> before = MatchmakingManager.PublishPresence;
		MatchmakingManager.PublishPresence = id => { lock (published) { published.Add(id); } };
		try
		{
			MatchmakingManager.RestoreQueue(new QueueLive { Tickets = new List<QueueTicketLive> { new() { UserId = 9301, PlaylistId = 0 } } }, 0);
			MatchmakingManager.DropExpiredRestoredTickets();

			Copy a = new(new FakeLiveStore(), "a");
			Assert.True(a.Lobbies.AdoptRestoredLobby(Lobby.FromLive(Stored(12, ELobbyState.INGAME, 9302, 9303)), 0));
			await a.Lobbies.Tick();
			lock (published)
			{
				Assert.Equal(new long[] { 9301, 9302, 9303 }, published.OrderBy(id => id).ToArray());
			}
		}
		finally
		{
			MatchmakingManager.PublishPresence = before;
			MatchmakingManager.RestoreQueue(new QueueLive(), 0);
		}
	}

	[Fact]
	public void AKeptSessionKeepsItsRelayCredentialsButOnlyAnOpenConnectionHidesItsPresenceChange()
	{
		// a member in its reconnect grace has no connection: its match goes on, its friends see it offline
		UserSession idle = new(9401, EUserSessionType.GameClient, "", "", "", 0, 0);
		RealtimeConnectionDrain.MarkSwitch(idle, kept: true);
		Assert.Equal((true, false), (idle.KeepRelayCredentials, idle.SwitchingAway));

		// a player the target keeps nothing for ends here as usual
		UserSession dropped = new(9402, EUserSessionType.GameClient, "", "", "", 0, 0);
		RealtimeConnectionDrain.MarkSwitch(dropped, kept: false);
		Assert.Equal((false, false), (dropped.KeepRelayCredentials, dropped.SwitchingAway));

		// a player whose lobby another copy took keeps the credentials of the match that goes on there
		UserSession lost = new(9403, EUserSessionType.GameClient, "", "", "", 0, 0);
		RealtimeConnectionDrain.MarkSwitch(lost, kept: false, keepRelay: true);
		Assert.Equal((true, false), (lost.KeepRelayCredentials, lost.SwitchingAway));
	}

	[Fact]
	public async Task TheMembersOfALobbyTheTargetDoesNotKeepSwitchWithoutASeatThere()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		Copy b = new(store, "b");
		(_, UserSession[] sessions) = await Live(a, Stored(13, ELobbyState.GAME_SETUP, 131, 132) with { Type = (int)ELobbyType.QuickMatch });

		using CancellationTokenSource stop = new();
		Task pump = b.PumpAsync(stop.Token);
		Task writer = a.PumpWriterAsync(stop.Token);
		Assert.True(await a.Handover.MoveAsync("b", 0, () => 0, () => Task.FromResult(true)));
		stop.Cancel();
		await Task.WhenAll(pump, writer);

		// a quick match setup stays out on the target, its members get no seat there and are not kept
		Assert.Null(b.Lobbies.GetLobby(13));
		Assert.Equal(new long[] { 131, 132 }, a.Switcher.Switches.Single().Users);
		Assert.False(a.Switcher.Switches.Single().Kept);
		GC.KeepAlive(sessions);
	}

	[Fact]
	public async Task AMemberWhoLeavesDuringTheMoveLeavesTheFrozenLobbyAloneAndGetsNoSeatOnTheTarget()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		Copy b = new(store, "b");
		(Lobby lobby, UserSession[] sessions) = await Live(a, Stored(14, ELobbyState.INGAME, 141, 142));
		LobbyManager.MovingLeave = a.Handover.OnMovingLeave;

		using CancellationTokenSource stop = new();
		Task writer = a.PumpWriterAsync(stop.Token);
		Task<bool> moving = a.Handover.MoveAsync("b", long.MaxValue, () => 0, () => Task.FromResult(true));
		await Until(() => lobby.IsMovingAway);

		// a ban ends the session of 142 while the lobby moves: the frozen lobby keeps its seat here, the leave goes to b
		Assert.False(await a.Lobbies.LeaveAsync(new LeaveLobbyCommand(14, 142, EUserSessionType.GameClient)));
		Assert.NotNull(lobby.GetMemberFromUserID(142));

		Task pump = b.PumpAsync(stop.Token);
		Assert.True(await moving);
		await Until(() => b.Lobbies.GetLobby(14) != null && b.Lobbies.GetLobby(14)!.GetMemberFromUserID(142) == null);
		stop.Cancel();
		await Task.WhenAll(pump, writer);
		LobbyManager.MovingLeave = null;
		Assert.NotNull(b.Lobbies.GetLobby(14)!.GetMemberFromUserID(141));
		GC.KeepAlive(sessions);
	}

	[Fact]
	public async Task AForcedMoveThatLandsDuringTheMeshCheckAwaitNeitherStartsNorAborts()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		(Lobby lobby, UserSession[] sessions) = await Live(a, Stored(16, ELobbyState.GAME_SETUP, 161, 162) with { Type = (int)ELobbyType.QuickMatch });

		// a start or abort that decided first holds the lobby, a forced move waits until it is released
		Assert.True(await lobby.TryReserveForStartAsync());
		Assert.Null(await lobby.BeginMoveAsync(force: true));
		lobby.ReleaseStartReservation();

		// a forced move that won the gate during the await: the bucket's start or abort is refused and nothing changes
		Assert.NotNull(await lobby.BeginMoveAsync(force: true));
		Assert.False(await lobby.TryReserveForStartAsync());
		Assert.Equal((ELobbyState.GAME_SETUP, 2), (lobby.State, lobby.GetNumberOfHumans()));
		GC.KeepAlive(sessions);
	}

	[Fact]
	public async Task AMemberWhoLogsOutDuringACancelledMoveHasNoSeatAndNoLeaveNoteIsLeft()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a", adoptTimeoutMs: 500);
		(Lobby lobby, UserSession[] sessions) = await Live(a, Stored(17, ELobbyState.GAME_SETUP, 171, 172));
		LobbyManager.MovingLeave = a.Handover.OnMovingLeave;
		try
		{
			using CancellationTokenSource stop = new();
			Task writer = a.PumpWriterAsync(stop.Token);
			Task<bool> moving = a.Handover.MoveAsync("b", long.MaxValue, () => 0, () => Task.FromResult(true));
			await Until(() => lobby.IsMovingAway);

			// 172 logs out while the lobby is frozen, b never takes the lobby and the move fails
			Assert.False(await a.Lobbies.LeaveAsync(new LeaveLobbyCommand(17, 172, EUserSessionType.GameClient)));
			Assert.NotNull(lobby.GetMemberFromUserID(172));
			Assert.False(await moving);
			await Until(() => store.State.ContainsKey(LiveKeys.Leave(17, 172)));

			// the cancel takes back the lobby and the note, applies the leave here and deletes the note
			await a.Handover.PrepareTakeBackAsync("b");
			a.Handover.OnActivating(FailoverActivation.TakenBack, 2);
			await Until(() => lobby.GetMemberFromUserID(172) == null && !store.State.ContainsKey(LiveKeys.Leave(17, 172)));
			stop.Cancel();
			await writer;
		}
		finally
		{
			LobbyManager.MovingLeave = null;
		}
		Assert.False(lobby.IsMovingAway);
		Assert.NotNull(lobby.GetMemberFromUserID(171));
		Assert.False(a.Handover.HasLocalLeaves(17));
		GC.KeepAlive(sessions);
	}

	[Fact]
	public async Task AQuickMatchSetupThatMovesNeitherStartsNorRequeuesItsPlayersHere()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		(Lobby lobby, UserSession[] sessions) = await Live(a, Stored(15, ELobbyState.GAME_SETUP, 151) with { Type = (int)ELobbyType.QuickMatch });
		UserSession player = sessions[0];
		player.IsRegisteredForMatchmaking = true;
		MatchmakingManager.MatchmakingBucket bucket = new(0, player, 2, 2, new ConcurrentList<int>(new[] { 0 }), lobby.Build);
		typeof(MatchmakingManager.MatchmakingBucket).GetField("m_LobbyID", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(bucket, 15L);
		Assert.False(bucket.FrozenOrReleasedByHandover(a.Lobbies));

		// while the lobby moves the bucket stays as it is
		Assert.NotNull(await lobby.BeginMoveAsync(force: true));
		Assert.True(bucket.FrozenOrReleasedByHandover(a.Lobbies));
		Assert.Equal((1, true), (bucket.CurrentMemberCount(), player.IsRegisteredForMatchmaking));

		// once it left, the bucket ends without an abort, a requeue or a status
		a.Lobbies.RemoveMovedLobby(lobby);
		Assert.True(bucket.FrozenOrReleasedByHandover(a.Lobbies));
		Assert.Equal((0, false), (bucket.CurrentMemberCount(), player.IsRegisteredForMatchmaking));
		Assert.DoesNotContain(MatchmakingManager.CaptureQueue().Tickets, t => t.UserId == 151);
	}

	[Fact]
	public async Task AFencedCopyDropsEveryLobbyAndOwnsNothing()
	{
		FakeLiveStore store = new();
		Copy a = new(store, "a");
		await Live(a, Stored(9, ELobbyState.INGAME, 91));

		a.Handover.OnFencing();
		Assert.Empty(a.Lobbies.GetLobbies());
		Assert.False(a.Writer.Owns(LiveKeys.Lobby(9)));
		a.Writer.Set(LiveKeys.Lobby(9), LiveJson.Data(Stored(9, ELobbyState.INGAME, 91)));
		await a.Writer.FlushAsync();
		Assert.Single(store.Stream);
	}
}
