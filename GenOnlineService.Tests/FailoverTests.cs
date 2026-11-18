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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GenOnlineService.Tests;

// The lease rules of docs/realtime-failover.md on a fake clock and an in-memory lease row with the database semantics
public class FailoverTests
{
	private sealed class Clock
	{
		public long Now = 1_000_000;
		public long Read() => Interlocked.Read(ref Now);
		public void Advance(long ms) => Interlocked.Add(ref Now, ms);
	}

	// The conditional updates of Database.Lease, with the fake clock as the database clock
	private sealed class FakeStore : ILeaseStore
	{
		private readonly Clock m_clock;
		private readonly object m_lock = new();
		public string Holder = String.Empty;
		public long Epoch;
		public long ExpiresAt;
		public string? Standby;
		public long StandbyExpiresAt;
		public bool HandoverRequested;
		public string? HandoverTarget;
		public bool HandoverConfirmed;
		public bool Down;
		public bool Hang;

		public FakeStore(Clock clock)
		{
			m_clock = clock;
		}

		private static DateTime At(long ms) => DateTime.UnixEpoch.AddMilliseconds(ms);

		private void Check()
		{
			if (Down)
			{
				throw new InvalidOperationException("database down");
			}
		}

		public Task<RealtimeLease?> ReadAsync(CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				Check();
				return Task.FromResult<RealtimeLease?>(new RealtimeLease { ID = 1, Holder = Holder, Epoch = Epoch, ExpiresAt = At(ExpiresAt), Standby = Standby, StandbyExpiresAt = At(StandbyExpiresAt), HandoverRequestedAt = HandoverRequested ? At(m_clock.Read()) : null, HandoverTarget = HandoverTarget, HandoverConfirmedAt = HandoverConfirmed ? At(m_clock.Read()) : null });
			}
		}

		public Task<long?> TryTakeAsync(string copyID, int leaseMs, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				Check();
				if (ExpiresAt >= m_clock.Read())
				{
					return Task.FromResult<long?>(null);
				}
				Holder = copyID;
				Epoch++;
				ExpiresAt = m_clock.Read() + leaseMs;
				Standby = null;
				HandoverRequested = false;
				HandoverTarget = null;
				HandoverConfirmed = false;
				return Task.FromResult<long?>(Epoch);
			}
		}

		public async Task<bool> RenewAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken)
		{
			if (Hang)
			{
				// a database path that drops packets: nothing answers until the caller gives up
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}
			return await RenewNowAsync(copyID, epoch, leaseMs);
		}

		private Task<bool> RenewNowAsync(string copyID, long epoch, int leaseMs)
		{
			lock (m_lock)
			{
				Check();
				if (Holder != copyID || Epoch != epoch)
				{
					return Task.FromResult(false);
				}
				ExpiresAt = m_clock.Read() + leaseMs;
				return Task.FromResult(true);
			}
		}

		public Task AnnounceStandbyAsync(string copyID, int leaseMs, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				Check();
				if (Holder != copyID && (HandoverTarget == null || HandoverTarget == copyID))
				{
					Standby = copyID;
					StandbyExpiresAt = m_clock.Read() + leaseMs;
				}
				return Task.CompletedTask;
			}
		}

		public Task<string?> BeginHandoverAsync(string copyID, long epoch, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				Check();
				long now = m_clock.Read();
				if (Holder != copyID || Epoch != epoch || ExpiresAt <= now || Standby == null || Standby == copyID || StandbyExpiresAt <= now)
				{
					return Task.FromResult<string?>(null);
				}
				HandoverTarget = Standby;
				HandoverConfirmed = false;
				HandoverRequested = false;
				return Task.FromResult<string?>(HandoverTarget);
			}
		}

		public Task<bool> ConfirmHandoverAsync(string copyID, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				Check();
				if (HandoverTarget != copyID || Holder == copyID || ExpiresAt <= m_clock.Read())
				{
					return Task.FromResult(false);
				}
				HandoverConfirmed = true;
				return Task.FromResult(true);
			}
		}

		public Task<bool> TargetAliveAsync(string copyID, long epoch, string target, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				Check();
				return Task.FromResult(Holder == copyID && Epoch == epoch && HandoverTarget == target && Standby == target && StandbyExpiresAt > m_clock.Read());
			}
		}

		public Task<long?> CancelHandoverAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				Check();
				if (Holder != copyID || Epoch != epoch)
				{
					return Task.FromResult<long?>(null);
				}
				Epoch++;
				ExpiresAt = m_clock.Read() + leaseMs;
				HandoverTarget = null;
				HandoverConfirmed = false;
				HandoverRequested = false;
				return Task.FromResult<long?>(Epoch);
			}
		}

		public Task<Database.Lease.Handover?> CompleteHandoverAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				Check();
				if (Holder != copyID || Epoch != epoch || HandoverTarget == null || !HandoverConfirmed || Standby != HandoverTarget || StandbyExpiresAt <= m_clock.Read())
				{
					return Task.FromResult<Database.Lease.Handover?>(null);
				}
				Holder = HandoverTarget;
				Epoch++;
				ExpiresAt = m_clock.Read() + leaseMs;
				Standby = null;
				HandoverTarget = null;
				HandoverConfirmed = false;
				return Task.FromResult<Database.Lease.Handover?>(new Database.Lease.Handover(Holder, Epoch, At(ExpiresAt)));
			}
		}

		public Task ReleaseAsync(string copyID, long epoch, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				Check();
				if (Holder == copyID && Epoch == epoch)
				{
					ExpiresAt = m_clock.Read() - 1000;
				}
				return Task.CompletedTask;
			}
		}

		public Task ClearHandoverRequestAsync(string copyID, long epoch, CancellationToken cancellationToken)
		{
			lock (m_lock)
			{
				HandoverRequested = false;
				return Task.CompletedTask;
			}
		}
	}

	private sealed class FakeDrain : IConnectionDrain
	{
		private int m_open;
		public readonly List<string> Closes = new();
		public readonly List<int> RetryAfters = new();
		public readonly List<int> Batches = new();
		public Action? OnSwitch;

		public FakeDrain(int open)
		{
			m_open = open;
		}

		public int OpenConnections => Volatile.Read(ref m_open);

		public Task<int> CloseAllAsync(string closeCode)
		{
			lock (Closes)
			{
				Closes.Add(closeCode);
			}
			return Task.FromResult(Interlocked.Exchange(ref m_open, 0));
		}

		public int Idle;

		public Task<int> EndIdleSessionsAsync() => Task.FromResult(Interlocked.Exchange(ref Idle, 0));

		public Task<int> SwitchAsync(int max, Func<int> retryAfterMs)
		{
			if (m_open > 0)
			{
				OnSwitch?.Invoke();
			}
			int n = Math.Min(max, m_open);
			for (int i = 0; i < n; i++)
			{
				RetryAfters.Add(retryAfterMs());
			}
			m_open -= n;
			if (n > 0)
			{
				Batches.Add(n);
			}
			return Task.FromResult(n);
		}
	}

	private static readonly FailoverSettings s_settings = new() { Enabled = true, BatchIntervalMs = 1, SwitchDelayMs = 1 };

	private static FailoverCoordinator Copy(string id, FakeStore store, FakeDrain drain, Clock clock, FailoverSettings? settings = null) => new(settings ?? s_settings, id, store, drain, clock.Read);

	// Moves the fake clock while a handover waits on it
	private static async Task<bool> ShutdownReturning(FailoverCoordinator copy)
	{
		await copy.ShutdownAsync();
		return true;
	}

	private static async Task<T> WithClockRunning<T>(Clock clock, Task<T> task, long stepMs = 100)
	{
		while (!task.IsCompleted)
		{
			clock.Advance(stepMs);
			await Task.Delay(5);
		}
		return await task;
	}

	[Fact]
	public void FailoverIsOffByDefaultWithTheDocumentedTimings()
	{
		FailoverSettings settings = FailoverSettings.Read(new ConfigurationBuilder().Build());
		Assert.False(settings.Enabled);
		Assert.Equal((500, 2500, 120, 5, 200), (settings.RenewIntervalMs, settings.LeaseMs, settings.HandoverLimitS, settings.BatchPercent, settings.BatchIntervalMs));

		IConfiguration on = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:failover:enabled"] = "true", ["Realtime:failover:lease_ms"] = "3000" }).Build();
		Assert.Equal((true, 3000), (FailoverSettings.Read(on).Enabled, FailoverSettings.Read(on).LeaseMs));

		Assert.Equal(1000, FailoverSettings.Read(new ConfigurationBuilder().Build()).SwitchDelayMs);

		// bad values matter only when failover is on
		IConfiguration badOff = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:failover:lease_ms"] = "1000" }).Build();
		Assert.False(FailoverSettings.Read(badOff).Enabled);
		IConfiguration bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Realtime:failover:enabled"] = "true", ["Realtime:failover:lease_ms"] = "1000" }).Build();
		Assert.Throws<InvalidOperationException>(() => FailoverSettings.Read(bad));
	}

	[Theory]
	[InlineData("mysql", "true", true)]
	[InlineData("mariadb", "true", true)]
	[InlineData("mysql", "false", false)]
	[InlineData("postgres", "true", false)]
	[InlineData(null, "true", false)]
	public void FailoverRefusesToStartOnMySqlAndMariaDb(string? provider, string enabled, bool refused)
	{
		IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:db_provider"] = provider, ["Realtime:failover:enabled"] = enabled }).Build();
		if (!refused)
		{
			Assert.Equal(enabled == "true", FailoverSettings.Read(config).Enabled);
			return;
		}
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => FailoverSettings.Read(config));
		Assert.Contains("Realtime:failover:enabled", ex.Message);
		Assert.Contains("PostgreSQL", ex.Message);
	}

	[Theory]
	[InlineData("mysql", "redis", true)]
	[InlineData("mariadb", "redis", true)]
	[InlineData("mysql", "none", false)]
	[InlineData("mariadb", null, false)]
	[InlineData("postgres", "redis", false)]
	[InlineData(null, "redis", false)]
	public void TheRedisBackplaneRefusesToStartOnMySqlAndMariaDb(string? provider, string? backplane, bool refused)
	{
		IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:db_provider"] = provider, ["Realtime:backplane"] = backplane, ["Realtime:redis"] = "127.0.0.1:6379" }).Build();
		if (!refused)
		{
			Assert.Equal(backplane == "redis", RealtimeBackplaneSettings.Read(config).UsesRedis);
			return;
		}
		InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => RealtimeBackplaneSettings.Read(config));
		Assert.Contains("Realtime:backplane", ex.Message);
		Assert.Contains("PostgreSQL", ex.Message);
	}

	[Fact]
	public void TheCopyIdIsTheHostNameWithARandomSuffix()
	{
		Assert.Matches("^.+-[0-9a-f]{8}$", CopyIdentity.ID);
		Assert.Same(CopyIdentity.ID, CopyIdentity.ID);
	}

	[Fact]
	public async Task ExactlyOneCopyHoldsTheLeaseAndIsReady()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FailoverCoordinator a = Copy("a", store, new FakeDrain(0), clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);

		Assert.False(a.IsReady);
		await a.TickAsync();
		await b.TickAsync();

		Assert.Equal((FailoverRole.Active, true, 1L), (a.Role, a.IsReady, a.Epoch));
		Assert.Equal((FailoverRole.Standby, false), (b.Role, b.IsReady));
		Assert.Equal("b", store.Standby);

		// renewals keep it while the standby waits
		for (int i = 0; i < 10; i++)
		{
			clock.Advance(500);
			await a.TickAsync();
			await b.TickAsync();
		}
		Assert.Equal(("a", 1L, true, false), (store.Holder, store.Epoch, a.IsReady, b.IsReady));
	}

	[Fact]
	public async Task ANewerEpochClosesEveryConnectionWithServerRestartingAtOnce()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drain = new(4);
		FailoverCoordinator a = Copy("a", store, drain, clock);
		await a.TickAsync();

		// another copy took over while this one was paused
		store.Holder = "b";
		store.Epoch++;
		clock.Advance(500);
		await a.TickAsync();
		await a.Closing;

		Assert.Equal((FailoverRole.Standby, false), (a.Role, a.IsReady));
		Assert.Equal(new[] { CloseCodes.ServerRestarting }, drain.Closes);
		Assert.Equal(0, drain.OpenConnections);
	}

	[Fact]
	public async Task NoRenewalBeforeTheOwnExpiryStopsServing()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drain = new(2);
		FailoverCoordinator a = Copy("a", store, drain, clock);
		await a.TickAsync();

		store.Down = true;
		clock.Advance(1000);
		await a.TickAsync();
		Assert.True(a.IsReady);
		Assert.Empty(drain.Closes);

		// readiness ends at the deadline even before the loop runs again
		clock.Advance(1500);
		Assert.False(a.IsReady);
		await a.TickAsync();
		await a.Closing;
		Assert.Equal(FailoverRole.Standby, a.Role);
		Assert.Equal(new[] { CloseCodes.ServerRestarting }, drain.Closes);
	}

	[Fact]
	public async Task AStandbyTakesTheExpiredLeaseWithTheNextEpochAndTheOldHolderStops()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainA = new(3);
		FailoverCoordinator a = Copy("a", store, drainA, clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		await a.TickAsync();
		await b.TickAsync();

		// a stops renewing, b waits for the expiry
		clock.Advance(2000);
		await b.TickAsync();
		Assert.False(b.IsReady);
		clock.Advance(600);
		await b.TickAsync();
		Assert.Equal((FailoverRole.Active, true, 2L), (b.Role, b.IsReady, b.Epoch));

		// a comes back and finds itself fenced
		Assert.False(a.IsReady);
		await a.TickAsync();
		await a.Closing;
		Assert.Equal(new[] { CloseCodes.ServerRestarting }, drainA.Closes);
		Assert.Equal(("b", 2L), (store.Holder, store.Epoch));
	}

	[Fact]
	public async Task APlannedSwitchMakesTheStandbyReadyFirstThenSwitchesConnectionsInBatches()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainA = new(40);
		FailoverCoordinator a = Copy("a", store, drainA, clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		await a.TickAsync();
		await b.TickAsync();

		bool targetReadyAtFirstSwitch = false;
		bool sourceReadyDuringSwitch = true;
		drainA.OnSwitch = () =>
		{
			targetReadyAtFirstSwitch |= drainA.Batches.Count == 0 && b.IsReady;
			sourceReadyDuringSwitch &= a.IsReady;
		};

		Task<bool> handover = a.HandOverAsync(closeWithoutStandby: true);
		while (a.Role == FailoverRole.Active)
		{
			await Task.Delay(5);
		}
		Assert.True(a.IsReady);
		clock.Advance(100);
		await b.TickAsync();

		Assert.True(await handover);
		Assert.True(targetReadyAtFirstSwitch);
		Assert.False(sourceReadyDuringSwitch);
		Assert.Equal((FailoverRole.Stopped, false), (a.Role, a.IsReady));

		// the lease moved last, the target confirms it with its next step
		Assert.Equal(("b", 2L), (store.Holder, store.Epoch));
		await b.TickAsync();
		Assert.Equal((FailoverRole.Active, 2L), (b.Role, b.Epoch));

		// 5 percent of 40 per batch, each connection with its own delay inside the range
		Assert.Equal(new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, drainA.Batches);
		Assert.Equal(40, drainA.RetryAfters.Count);
		Assert.All(drainA.RetryAfters, ms => Assert.InRange(ms, 250, 1000));
		Assert.Empty(drainA.Closes);
	}

	[Fact]
	public async Task AShutdownWithoutAStandbyClosesWithServerRestartingAndReleasesTheLease()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drain = new(3);
		FailoverCoordinator a = Copy("a", store, drain, clock);
		await a.TickAsync();

		Assert.False(await a.HandOverAsync(closeWithoutStandby: true));
		Assert.Equal(new[] { CloseCodes.ServerRestarting }, drain.Closes);
		Assert.Equal((FailoverRole.Stopped, false), (a.Role, a.IsReady));

		// the next copy takes the released lease without waiting for the expiry
		FailoverCoordinator next = Copy("next", store, new FakeDrain(0), clock);
		await next.TickAsync();
		Assert.Equal((FailoverRole.Active, 2L), (next.Role, next.Epoch));
	}

	[Fact]
	public async Task AnOperatorHandoverWithoutAStandbyKeepsServing()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drain = new(3);
		FailoverCoordinator a = Copy("a", store, drain, clock);
		await a.TickAsync();

		store.HandoverRequested = true;
		clock.Advance(500);
		await a.TickAsync();
		Assert.True(a.TakeHandoverRequest());
		Assert.False(a.TakeHandoverRequest());

		Assert.False(await a.HandOverAsync(closeWithoutStandby: false));
		Assert.Equal((FailoverRole.Active, true), (a.Role, a.IsReady));
		Assert.Empty(drain.Closes);
		Assert.False(store.HandoverRequested);
	}

	[Fact]
	public async Task AShuttingDownStandbyNeverTakesTheLease()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);

		Assert.False(await b.HandOverAsync(closeWithoutStandby: true));
		await b.TickAsync();
		Assert.Equal((FailoverRole.Stopped, String.Empty), (b.Role, store.Holder));
	}

	[Fact]
	public async Task TheDeadlineFencesTheCopyWhileTheDatabaseHangs()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drain = new(5);
		FailoverSettings settings = new() { Enabled = true, RenewIntervalMs = 100, LeaseMs = 300 };
		FailoverCoordinator a = Copy("a", store, drain, clock, settings);
		await a.TickAsync();
		Assert.False(a.IsFenced);

		// the renewal hangs, the deadline check alone stops the copy
		store.Hang = true;
		clock.Advance(100);
		Task tick = a.TickAsync();
		clock.Advance(250);
		a.CheckDeadline();
		await a.Closing;
		Assert.Equal((FailoverRole.Standby, false, true), (a.Role, a.IsReady, a.IsFenced));
		Assert.Equal(new[] { CloseCodes.ServerRestarting }, drain.Closes);

		// the hanging statement is cancelled at the deadline instead of waiting for a command timeout
		Task finished = await Task.WhenAny(tick, Task.Delay(5000));
		Assert.Same(tick, finished);
		Assert.Equal(new[] { CloseCodes.ServerRestarting }, drain.Closes);
	}

	[Fact]
	public async Task OnlyTheHolderAndTheHandoverOverlapAreNotFenced()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FailoverCoordinator a = Copy("a", store, new FakeDrain(0), clock);
		Assert.True(a.IsFenced);
		await a.TickAsync();
		Assert.False(a.IsFenced);
		clock.Advance(2500);
		Assert.True(a.IsFenced);
	}

	private sealed class FakeCaller : Microsoft.AspNetCore.SignalR.HubCallerContext
	{
		public bool Aborted;
		public override string ConnectionId => "c1";
		public override string? UserIdentifier => "1";
		public override System.Security.Claims.ClaimsPrincipal? User => null;
		public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
		public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
		public override CancellationToken ConnectionAborted => CancellationToken.None;
		public override void Abort() => Aborted = true;
	}

	private sealed class EmptyHub : Microsoft.AspNetCore.SignalR.Hub
	{
		public void Ping() { }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AHubCallOnAFencedCopyIsRefused(bool fenced)
	{
		FakeCaller caller = new();
		var invocation = new Microsoft.AspNetCore.SignalR.HubInvocationContext(caller, new ServiceCollection().BuildServiceProvider(), new EmptyHub(), typeof(EmptyHub).GetMethod(nameof(EmptyHub.Ping))!, Array.Empty<object?>());
		bool ran = false;
		RealtimeFenceFilter filter = new(() => fenced);

		object? result = await filter.InvokeMethodAsync(invocation, _ =>
		{
			ran = true;
			return ValueTask.FromResult<object?>("ok");
		});

		Assert.Equal((!fenced, fenced), (ran, caller.Aborted));
		Assert.Equal(fenced ? null : "ok", result);

		bool connected = false;
		FakeCaller opening = new();
		await filter.OnConnectedAsync(new Microsoft.AspNetCore.SignalR.HubLifetimeContext(opening, new ServiceCollection().BuildServiceProvider(), new EmptyHub()), _ =>
		{
			connected = true;
			return Task.CompletedTask;
		});
		Assert.Equal((!fenced, fenced), (connected, opening.Aborted));
	}

	[Fact]
	public async Task ATargetThatNeverConfirmsCancelsTheSwitchAndTheCopyTakesTheLeaseBack()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drain = new(10);
		FailoverCoordinator a = Copy("a", store, drain, clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		await a.TickAsync();
		await b.TickAsync();

		// b dies right after it was named the target, the holder kept the lease and cancels with the next epoch
		Assert.False(await WithClockRunning(clock, a.HandOverAsync(closeWithoutStandby: false)));

		Assert.Equal((FailoverRole.Active, true, 2L), (a.Role, a.IsReady, a.Epoch));
		Assert.Equal(("a", 2L, (string?)null), (store.Holder, store.Epoch, store.HandoverTarget));
		Assert.Empty(drain.Batches);
		Assert.Empty(drain.Closes);
		Assert.Equal(10, drain.OpenConnections);

		// it keeps renewing as the holder
		await a.TickAsync();
		Assert.True(a.IsReady);
		Assert.Equal(FailoverRole.Active, a.Role);
	}

	[Fact]
	public async Task AShutdownWhoseTargetNeverConfirmsStillClosesWithServerRestartingAndReleasesTheLease()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drain = new(4);
		FailoverCoordinator a = Copy("a", store, drain, clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		await a.TickAsync();
		await b.TickAsync();

		// b dies right after the lease was handed to it, a takes it back and must still stop cleanly
		await WithClockRunning(clock, ShutdownReturning(a));

		Assert.Empty(drain.Batches);
		Assert.Equal(new[] { CloseCodes.ServerRestarting }, drain.Closes);
		Assert.Equal((FailoverRole.Stopped, false), (a.Role, a.IsReady));

		// the next copy takes the released lease without waiting for the expiry
		FailoverCoordinator next = Copy("next", store, new FakeDrain(0), clock);
		await next.TickAsync();
		Assert.Equal(FailoverRole.Active, next.Role);
	}

	[Fact]
	public async Task ATargetLeaseThatLapsesDuringTheDrainStopsTheDrainAndTheCopyServesAgain()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainA = new(40);
		FailoverCoordinator a = Copy("a", store, drainA, clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		await a.TickAsync();
		await b.TickAsync();

		// b confirms, then stops renewing during the first batch
		drainA.OnSwitch = () =>
		{
			if (drainA.Batches.Count == 0)
			{
				clock.Advance(3000);
			}
		};
		Task<bool> handover = a.HandOverAsync(closeWithoutStandby: false);
		while (a.Role == FailoverRole.Active)
		{
			await Task.Delay(5);
		}
		clock.Advance(100);
		await b.TickAsync();

		Assert.False(await handover);
		Assert.Equal((FailoverRole.Active, true, 2L), (a.Role, a.IsReady, a.Epoch));
		Assert.Equal(new[] { 2 }, drainA.Batches);
		Assert.Equal(38, drainA.OpenConnections);
		Assert.Empty(drainA.Closes);
	}

	[Fact]
	public async Task ACancelledOperatorHandoverKeepsTheApplicationRunning()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FailoverSettings settings = new() { Enabled = true, RenewIntervalMs = 50, LeaseMs = 2500, SwitchDelayMs = 1, BatchIntervalMs = 1 };
		FailoverCoordinator a = Copy("a", store, new FakeDrain(3), clock, settings);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock, settings);

		bool stopped = false;
		using CancellationTokenSource stop = new();
		Task loop = a.RunAsync(() => stopped = true, stop.Token);
		while (a.Role != FailoverRole.Active)
		{
			await Task.Delay(5);
		}
		await b.TickAsync();
		store.HandoverRequested = true;

		// the clock runs slower than the renewals, b never confirms
		long until = Environment.TickCount64 + 10_000;
		while (store.Epoch < 2 && Environment.TickCount64 < until)
		{
			clock.Advance(20);
			await Task.Delay(5);
		}
		await Task.Delay(200);

		Assert.False(stopped);
		Assert.Equal(("a", 2L, FailoverRole.Active), (store.Holder, store.Epoch, a.Role));
		stop.Cancel();
		await loop;
		Assert.False(stopped);
	}

	[Fact]
	public async Task TheShutdownSignalWaitsForARunningOperatorDrain()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainA = new(40);
		FailoverSettings settings = new() { Enabled = true, BatchIntervalMs = 20, SwitchDelayMs = 1 };
		FailoverCoordinator a = Copy("a", store, drainA, clock, settings);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock, settings);
		await a.TickAsync();
		await b.TickAsync();

		Task<bool> operatorHandover = a.HandOverAsync(closeWithoutStandby: false);
		while (a.Role == FailoverRole.Active)
		{
			await Task.Delay(5);
		}
		clock.Advance(100);
		await b.TickAsync();
		while (drainA.Batches.Count == 0)
		{
			await Task.Delay(5);
		}

		await a.ShutdownAsync();
		Assert.True(operatorHandover.IsCompleted);
		Assert.True(await operatorHandover);
		Assert.Equal(40, drainA.RetryAfters.Count);
		Assert.Empty(drainA.Closes);
		Assert.Equal(FailoverRole.Stopped, a.Role);
	}

	[Fact]
	public async Task ConnectionsLeftAtTheHandoverLimitStillGetServerSwitchingAfterTheSwitchDelay()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainA = new(40) { Idle = 2 };
		FailoverSettings settings = new() { Enabled = true, BatchIntervalMs = 1, SwitchDelayMs = 300, HandoverLimitS = 1 };
		FailoverCoordinator a = Copy("a", store, drainA, clock, settings);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock, settings);
		await a.TickAsync();
		await b.TickAsync();

		System.Diagnostics.Stopwatch sinceConfirm = new();
		long firstSwitchMs = -1;
		drainA.OnSwitch = () =>
		{
			if (drainA.Batches.Count == 0)
			{
				firstSwitchMs = sinceConfirm.ElapsedMilliseconds;
				clock.Advance(1500);
			}
		};
		Task<bool> handover = a.HandOverAsync(closeWithoutStandby: true);
		while (a.Role == FailoverRole.Active)
		{
			await Task.Delay(5);
		}
		clock.Advance(100);
		await b.TickAsync();
		sinceConfirm.Start();

		// the target keeps renewing while the limit passes
		Task renewing = Task.Run(async () =>
		{
			while (!handover.IsCompleted)
			{
				await b.TickAsync();
				await Task.Delay(5);
			}
		});
		Assert.True(await handover);
		await renewing;

		Assert.InRange(firstSwitchMs, 250, 5000);
		Assert.Equal(new[] { 2, 38 }, drainA.Batches);
		Assert.Equal(40, drainA.RetryAfters.Count);
		Assert.Empty(drainA.Closes);
		Assert.Equal(0, drainA.Idle);
	}

	private static async Task<HttpContext> Send(bool serving, string path)
	{
		ServiceCollection services = new();
		services.AddLogging();
		ApplicationBuilder app = new(services.BuildServiceProvider());
		app.UseRealtimeStandbyGate(() => serving);
		app.Run(context =>
		{
			context.Response.StatusCode = StatusCodes.Status200OK;
			return Task.CompletedTask;
		});

		DefaultHttpContext context = new();
		context.Request.Path = path;
		context.Response.Body = new MemoryStream();
		await app.Build()(context);
		return context;
	}

	[Theory]
	[InlineData(true, "/realtime", 200)]
	[InlineData(true, "/api/v1/me", 200)]
	[InlineData(false, "/realtime", 503)]
	[InlineData(false, "/api/v1/auth/session", 503)]
	[InlineData(false, "/-/readiness", 200)]
	[InlineData(false, "/-/liveness", 200)]
	[InlineData(false, "/oauth/token", 200)]
	public async Task AStandbyRefusesTheHubUpgradeAndGameApiCallsWithARetryable503(bool serving, string path, int status)
	{
		HttpContext context = await Send(serving, path);
		Assert.Equal(status, context.Response.StatusCode);
		if (status == 503)
		{
			Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
			context.Response.Body.Position = 0;
			Assert.Equal("{\"error\":\"service.unavailable\",\"params\":{\"what\":\"standby\"}}", new StreamReader(context.Response.Body).ReadToEnd());
		}
	}

	[Fact]
	public void WithoutFailoverEveryCopyServes()
	{
		Assert.False(RealtimeFailover.Enabled);
		Assert.True(RealtimeFailover.Serving);
	}

	[Fact]
	public async Task AStandbyThatIsNotInSyncNeverAnnouncesItselfSoNoHandoverGoesToIt()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FailoverCoordinator a = Copy("a", store, new FakeDrain(2), clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		bool inSync = false;
		b.StandbyReady = () => inSync;
		await a.TickAsync();
		await b.TickAsync();

		Assert.Null(store.Standby);
		Assert.False(await a.HandOverAsync(closeWithoutStandby: false));
		Assert.Equal(FailoverRole.Active, a.Role);

		inSync = true;
		clock.Advance(500);
		await a.TickAsync();
		await b.TickAsync();
		Assert.Equal("b", store.Standby);

		// an entry that lapses after the standby fell out of sync takes no handover either
		inSync = false;
		clock.Advance(3000);
		await a.TickAsync();
		await b.TickAsync();
		Assert.False(await a.HandOverAsync(closeWithoutStandby: false));
	}

	[Fact]
	public async Task TheActivationHookRunsBeforeTheCopyReportsReadyAndNamesHowItGotTheLease()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FailoverCoordinator a = Copy("a", store, new FakeDrain(0), clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		List<(FailoverActivation Activation, long Epoch, bool ReadyDuringHook)> calls = new();
		a.Activating = (activation, epoch) => calls.Add((activation, epoch, a.IsReady));
		b.Activating = (activation, epoch) => calls.Add((activation, epoch, b.IsReady));

		await a.TickAsync();
		await b.TickAsync();
		Assert.Equal(new[] { (FailoverActivation.FirstTake, 1L, false) }, calls);

		// a stops renewing, b takes the expired lease after an unplanned failure
		clock.Advance(2600);
		await b.TickAsync();
		Assert.Equal((FailoverActivation.AfterExpiry, 2L, false), calls.Last());
		Assert.True(b.IsReady);

		// a hook that throws still lets the copy serve
		FailoverCoordinator c = Copy("c", new FakeStore(clock), new FakeDrain(0), clock);
		c.Activating = (_, _) => throw new InvalidOperationException("restore failed");
		await c.TickAsync();
		Assert.True(c.IsReady);
	}

	private sealed class FakeMover : IHandoverMover
	{
		public readonly List<string> Calls = new();
		public bool Result = true;
		public Func<Task>? During;

		public async Task<bool> MoveAsync(string target, long limitAtMs, Func<long> clockMs, Func<Task<bool>> targetAlive)
		{
			lock (Calls)
			{
				Calls.Add("move " + target);
			}
			if (During != null)
			{
				await During();
			}
			return Result && await targetAlive();
		}

		public Task PrepareTakeBackAsync(string target)
		{
			lock (Calls)
			{
				Calls.Add("take back " + target);
			}
			return Task.CompletedTask;
		}
	}

	// Ticks both copies while the fake clock runs, as their lease loops do
	private static async Task<T> WithLoops<T>(Clock clock, Task<T> task, params FailoverCoordinator[] copies)
	{
		while (!task.IsCompleted)
		{
			clock.Advance(100);
			foreach (FailoverCoordinator copy in copies)
			{
				await copy.TickAsync();
			}
			await Task.Delay(5);
		}
		return await task;
	}

	[Fact]
	public async Task TheHolderKeepsAndRenewsTheLeaseDuringItsHandoverAndHandsItOverLast()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainA = new(40);
		FailoverSettings settings = new() { Enabled = true, BatchIntervalMs = 20, SwitchDelayMs = 1 };
		FailoverCoordinator a = Copy("a", store, drainA, clock, settings);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock, settings);
		await a.TickAsync();
		await b.TickAsync();

		List<(string Holder, long ExpiresAt, FailoverRole Target)> seen = new();
		drainA.OnSwitch = () => seen.Add((store.Holder, store.ExpiresAt, b.Role));
		Assert.True(await WithLoops(clock, a.HandOverAsync(closeWithoutStandby: true), a, b));

		// every batch ran while a held the lease and renewed it, with b serving as the target
		Assert.Equal(20, seen.Count);
		Assert.All(seen, s => Assert.Equal(("a", FailoverRole.Target), (s.Holder, s.Target)));
		Assert.True(seen.Last().ExpiresAt > seen.First().ExpiresAt);
		Assert.Equal(("b", 2L), (store.Holder, store.Epoch));
		await b.TickAsync();
		Assert.Equal((FailoverRole.Active, true), (b.Role, b.IsReady));
	}

	[Fact]
	public async Task TheTargetServesUntilTheHolderCancelsThenStepsDownAndClosesItsConnections()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainB = new(5);
		FailoverCoordinator a = Copy("a", store, new FakeDrain(0), clock);
		FailoverCoordinator b = Copy("b", store, drainB, clock);
		int fenced = 0;
		b.Fencing = () => fenced += drainB.Closes.Count == 0 ? 1 : 100;
		await a.TickAsync();
		await b.TickAsync();

		store.HandoverTarget = "b";
		await b.TickAsync();
		Assert.Equal((FailoverRole.Target, true, false), (b.Role, b.IsReady, b.IsFenced));
		Assert.True(store.HandoverConfirmed);

		// the holder ends the handover without moving the lease
		store.HandoverTarget = null;
		await b.TickAsync();
		await b.Closing;
		Assert.Equal((FailoverRole.Standby, false), (b.Role, b.IsReady));
		Assert.Equal(new[] { CloseCodes.ServerRestarting }, drainB.Closes);
		Assert.Equal(1, fenced);
	}

	[Fact]
	public async Task AHolderThatDiesDuringTheHandoverLetsTheTargetTakeTheLeaseAndEverything()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FailoverCoordinator a = Copy("a", store, new FakeDrain(0), clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		List<FailoverActivation> activations = new();
		b.Activating = (activation, _) => activations.Add(activation);
		await a.TickAsync();
		await b.TickAsync();
		store.HandoverTarget = "b";
		await b.TickAsync();
		Assert.Equal(FailoverRole.Target, b.Role);

		// a stops renewing, b takes the expired lease as after any failure
		clock.Advance(2600);
		await b.TickAsync();
		Assert.Equal((FailoverRole.Active, 2L, (string?)null), (b.Role, b.Epoch, store.HandoverTarget));
		Assert.Equal(new[] { FailoverActivation.AfterExpiry }, activations);
	}

	[Fact]
	public async Task TheMovesRunBeforeTheBatchesAndAFailedMoveCancelsWithATakeBack()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainA = new(10);
		FailoverCoordinator a = Copy("a", store, drainA, clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		FakeMover mover = new() { Result = false };
		a.Mover = mover;
		List<(FailoverActivation Activation, long Epoch)> activations = new();
		a.Activating = (activation, epoch) => activations.Add((activation, epoch));
		await a.TickAsync();
		await b.TickAsync();
		activations.Clear();

		Assert.False(await WithLoops(clock, a.HandOverAsync(closeWithoutStandby: false), a, b));
		Assert.Equal(new[] { "move b", "take back b" }, mover.Calls);
		Assert.Empty(drainA.Batches);
		Assert.Equal(new[] { (FailoverActivation.TakenBack, 2L) }, activations);
		Assert.Equal((FailoverRole.Active, true, "a", 2L), (a.Role, a.IsReady, store.Holder, store.Epoch));

		// the target sees the cancel and steps down
		await b.TickAsync();
		Assert.Equal(FailoverRole.Standby, b.Role);
	}

	[Fact]
	public async Task ASuccessfulMoveComesBeforeTheFirstBatch()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainA = new(4);
		FailoverCoordinator a = Copy("a", store, drainA, clock);
		FailoverCoordinator b = Copy("b", store, new FakeDrain(0), clock);
		FakeMover mover = new();
		int batchesAtMove = -1;
		mover.During = () =>
		{
			batchesAtMove = drainA.Batches.Count;
			return Task.CompletedTask;
		};
		a.Mover = mover;
		await a.TickAsync();
		await b.TickAsync();

		Assert.True(await WithLoops(clock, a.HandOverAsync(closeWithoutStandby: true), a, b));
		Assert.Equal(0, batchesAtMove);
		Assert.Equal(4, drainA.RetryAfters.Count);
	}

	[Fact]
	public async Task ALostLeaseDropsTheOwnedStateBeforeTheConnectionsClose()
	{
		Clock clock = new();
		FakeStore store = new(clock);
		FakeDrain drainA = new(3);
		FailoverCoordinator a = Copy("a", store, drainA, clock);
		int closesAtFencing = -1;
		a.Fencing = () => closesAtFencing = drainA.Closes.Count;
		await a.TickAsync();

		store.Epoch = 9;
		clock.Advance(500);
		await a.TickAsync();
		await a.Closing;
		Assert.Equal(0, closesAtFencing);
		Assert.Equal(new[] { CloseCodes.ServerRestarting }, drainA.Closes);
	}
}
