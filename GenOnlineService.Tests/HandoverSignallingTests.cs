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
using System.Reflection;
using Xunit;

namespace GenOnlineService.Tests;

// The members of a lobby a handover moved reconnect together: a pair whose two members both come back restarts once,
// after both SetPeerTransports arrived or after the grace when one of them is late (docs/realtime-failover.md)
[Collection(LiveStateCollection.Name)]
public class HandoverSignallingTests
{
	private const long A = 11;
	private const long B = 12;
	private const int GraceMs = 2000;

	// Records every StartSignalling sent to a member's game connection
	public class RecordingClient : DispatchProxy
	{
		public long Recipient;
		public List<(long Recipient, StartSignallingEvent Event)> Sink = null!;

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			if (targetMethod?.Name == nameof(IRealtimeClient.StartSignalling))
			{
				lock (Sink)
				{
					Sink.Add((Recipient, (StartSignallingEvent)args![0]!));
				}
			}
			return Task.CompletedTask;
		}
	}

	// Timers that fire only when the test moves the clock
	private sealed class ManualClock : TimeProvider
	{
		private readonly List<ManualTimer> m_timers = new();
		private long m_nowMs;

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			ManualTimer timer = new(callback, state, m_nowMs + (long)dueTime.TotalMilliseconds);
			m_timers.Add(timer);
			return timer;
		}

		public void Advance(int ms)
		{
			m_nowMs += ms;
			foreach (ManualTimer timer in m_timers.ToArray())
			{
				if (!timer.Disposed && !timer.Fired && timer.DueMs <= m_nowMs)
				{
					timer.Fired = true;
					timer.Callback(timer.State);
				}
			}
		}

		public int Waiting => m_timers.Count(timer => !timer.Disposed && !timer.Fired);
	}

	private sealed class ManualTimer : ITimer
	{
		public readonly TimerCallback Callback;
		public readonly object? State;
		public long DueMs;
		public bool Fired;
		public bool Disposed;

		public ManualTimer(TimerCallback callback, object? state, long dueMs)
		{
			Callback = callback;
			State = state;
			DueMs = dueMs;
		}

		public bool Change(TimeSpan dueTime, TimeSpan period) => false;
		public void Dispose() => Disposed = true;
		public ValueTask DisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class Rig
	{
		public readonly Lobby Lobby;
		public readonly ManualClock Clock = new();
		public readonly List<(long Recipient, StartSignallingEvent Event)> Sent = new();
		public readonly Dictionary<long, UserSession> Sessions = new();
		private readonly Dictionary<long, object> m_connections = new();

		public Rig(params long[] users)
		{
			Lobby = Lobby.FromLive(Stored(users));
			Lobby.Clock = Clock;
			Lobby.GameClientOf = member =>
			{
				IRealtimeClient client = DispatchProxy.Create<IRealtimeClient, RecordingClient>();
				RecordingClient recorder = (RecordingClient)(object)client;
				recorder.Recipient = member.UserID;

				// a member without a live connection gets nothing, as RealtimeLobbies.GameOf answers
				recorder.Sink = member.HasLiveSession() ? Sent : new();
				return client;
			};
		}

		// The member's game client connects to the copy that took the lobby over and takes its restored seat, as RealtimeHub.OnConnectedAsync does
		public async Task ReturnAsync(long user)
		{
			UserSession session = new(user, EUserSessionType.GameClient, "", "", "", 0, 0);
			object connection = new();
			session.ResetPeerTransports(connection);
			Assert.True(await Lobby.AttachRestoredMemberAsync(session));
			Sessions[user] = session;
			m_connections[user] = connection;
			session.MarkSignallingResendPending();
		}

		// A reconnect of a member that is still seated, on a new connection of its session
		public void Reconnect(long user)
		{
			object connection = new();
			Sessions[user].ResetPeerTransports(connection);
			m_connections[user] = connection;
			Sessions[user].MarkSignallingResendPending();
		}

		// SetPeerTransports of the member's current connection, then the pending reconnect resend, as RealtimeHub.SetPeerTransports does
		public async Task SetPeerTransportsAsync(long user)
		{
			UserSession session = Sessions[user];
			Assert.True(session.SetPeerTransports(m_connections[user], Array.Empty<string>()));
			if (session.TakeSignallingResend())
			{
				await Lobby.ResendSignallingAfterReconnectAsync(user);
			}
		}

		// Moves the clock and lets a deferred restart that fired finish under the lobby gate
		public async Task AdvanceAsync(int ms)
		{
			Clock.Advance(ms);
			await Lobby.RunExclusiveAsync(() => Task.CompletedTask);
		}

		public int To(long recipient, long peer)
		{
			lock (Sent)
			{
				return Sent.Count(sent => sent.Recipient == recipient && sent.Event.UserId == peer);
			}
		}

		public int Count
		{
			get
			{
				lock (Sent)
				{
					return Sent.Count;
				}
			}
		}
	}

	private static LobbyMemberLive Slot(int index, long user) => user > 0
		? new() { SlotIndex = index, SlotState = (int)EPlayerType.SLOT_PLAYER, UserId = user, DisplayName = "u" + user, NameNotDeduped = "u" + user, Port = 8088 }
		: new() { SlotIndex = index, SlotState = (int)(index < 4 ? EPlayerType.SLOT_OPEN : EPlayerType.SLOT_CLOSED) };

	// A custom game in setup with no countdown, as a handover leaves it on the copy that took it over
	private static LobbyLive Stored(long[] users) => new()
	{
		Id = 405,
		Type = (int)ELobbyType.CustomGame,
		State = (int)ELobbyState.GAME_SETUP,
		Owner = users[0],
		Name = "lobby 405",
		CreatedAt = new DateTime(2026, 11, 17, 13, 0, 0, DateTimeKind.Utc),
		Build = new LiveBuild { Game = "genonline_zerohour" },
		StartPhase = "none",
		Slots = Enumerable.Range(0, Lobby.MaxLobbySize).Select(i => Slot(i, i < users.Length ? users[i] : 0)).ToList(),
		EverMembers = users.ToList()
	};

	[Fact]
	public void TheGraceDefaultsToTwoSeconds()
	{
		Assert.Equal(GraceMs, HandoverSignallingSettings.GraceMs);
	}

	[Fact]
	public async Task BothMembersComingBackRestartTheirPairOnceAfterTheSecondSetPeerTransports()
	{
		Rig rig = new(A, B);
		await rig.ReturnAsync(A);
		await rig.ReturnAsync(B);

		await rig.SetPeerTransportsAsync(A);
		Assert.Equal(0, rig.Count);

		await rig.SetPeerTransportsAsync(B);
		Assert.Equal(1, rig.To(A, B));
		Assert.Equal(1, rig.To(B, A));
		Assert.Equal(2, rig.Count);
		Assert.Equal(rig.Sent[0].Event.Attempt, rig.Sent[1].Event.Attempt);

		// the grace of the first member finds its pair already restarted
		await rig.AdvanceAsync(GraceMs * 2);
		Assert.Equal(2, rig.Count);
		Assert.Equal(0, rig.Clock.Waiting);
	}

	[Fact]
	public async Task ALateMembersPairRestartsOnceWhenTheGraceExpires()
	{
		Rig rig = new(A, B);
		await rig.ReturnAsync(A);
		await rig.ReturnAsync(B);

		await rig.SetPeerTransportsAsync(A);
		await rig.AdvanceAsync(GraceMs - 1);
		Assert.Equal(0, rig.Count);

		await rig.AdvanceAsync(1);
		Assert.Equal(1, rig.To(A, B));
		Assert.Equal(1, rig.To(B, A));

		// the late SetPeerTransports of the same handover finds the pair already restarted
		await rig.SetPeerTransportsAsync(B);
		await rig.AdvanceAsync(GraceMs * 2);
		Assert.Equal(2, rig.Count);
	}

	[Fact]
	public async Task AMemberComingBackAloneRestartsItsPairsAtOnce()
	{
		Rig rig = new(A, B);

		// b has not reconnected yet, so a has no pair to restart, as before
		await rig.ReturnAsync(A);
		await rig.SetPeerTransportsAsync(A);
		Assert.Equal(0, rig.Count);

		// b comes back after a finished its own reconnect: its pair restarts at once, without waiting for the grace
		await rig.ReturnAsync(B);
		await rig.SetPeerTransportsAsync(B);
		Assert.Equal(1, rig.To(A, B));
		Assert.Equal(1, rig.To(B, A));
		Assert.Equal(0, rig.Clock.Waiting);
	}

	[Fact]
	public async Task AReconnectAfterTheHandoverStillRestartsItsPairsAtOnce()
	{
		Rig rig = new(A, B);
		await rig.ReturnAsync(A);
		await rig.ReturnAsync(B);
		await rig.SetPeerTransportsAsync(A);
		await rig.SetPeerTransportsAsync(B);
		Assert.Equal(2, rig.Count);

		// a later reconnect of one member on its own is no handover return: every unconnected pair restarts when its transports arrive
		rig.Reconnect(A);
		Assert.Equal(2, rig.Count);
		await rig.SetPeerTransportsAsync(A);
		Assert.Equal(2, rig.To(A, B));
		Assert.Equal(2, rig.To(B, A));
		Assert.Equal(0, rig.Clock.Waiting);
	}

	[Fact]
	public async Task AGameStartedDuringTheGraceRestartsNothing()
	{
		Rig rig = new(A, B);
		await rig.ReturnAsync(A);
		await rig.ReturnAsync(B);
		await rig.SetPeerTransportsAsync(A);

		Assert.True(await rig.Lobby.TryStartMatch());
		await rig.AdvanceAsync(GraceMs);
		await rig.SetPeerTransportsAsync(B);
		Assert.Equal(0, rig.Count);
	}

	[Fact]
	public async Task AMemberWhoLeftDuringTheGraceGetsNothing()
	{
		Rig rig = new(A, B);
		await rig.ReturnAsync(A);
		await rig.ReturnAsync(B);
		await rig.SetPeerTransportsAsync(A);

		Assert.NotNull(await rig.Lobby.RemoveMemberAsync(B));
		await rig.AdvanceAsync(GraceMs);
		Assert.Equal(0, rig.Count);
	}

	[Fact]
	public async Task ALobbyThatLeftThisCopyDuringTheGraceRestartsNothing()
	{
		Rig rig = new(A, B);
		await rig.ReturnAsync(A);
		await rig.ReturnAsync(B);
		await rig.SetPeerTransportsAsync(A);

		rig.Lobby.MarkReplaced();
		await rig.AdvanceAsync(GraceMs);
		Assert.Equal(0, rig.Count);
	}
}
