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

using GenOnlineService.Realtime;

namespace GenOnlineService.Failover
{
	public enum FailoverRole
	{
		Standby,
		Active,
		HandingOver,
		Draining,
		Stopped
	}

	// The lease statements of Database.Lease, behind an interface so the coordinator runs without a database in tests
	public interface ILeaseStore
	{
		Task<RealtimeLease?> ReadAsync(CancellationToken cancellationToken);
		Task<long?> TryTakeAsync(string copyID, int leaseMs, CancellationToken cancellationToken);
		Task<bool> RenewAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken);
		Task AnnounceStandbyAsync(string copyID, int leaseMs, CancellationToken cancellationToken);
		Task<Database.Lease.Handover?> HandOverAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken);
		Task<long?> ReclaimAsync(string copyID, Database.Lease.Handover handover, int leaseMs, CancellationToken cancellationToken);
		Task ReleaseAsync(string copyID, long epoch, CancellationToken cancellationToken);
		Task ClearHandoverRequestAsync(string copyID, long epoch, CancellationToken cancellationToken);
	}

	// The realtime connections of this copy
	public interface IConnectionDrain
	{
		int OpenConnections { get; }

		// Closes every connection at once and destroys every session
		Task<int> CloseAllAsync(string closeCode);

		// Sends ServerSwitching and closes with server.switching up to max open connections, returns how many
		Task<int> SwitchAsync(int max, Func<int> retryAfterMs);

		// Destroys the sessions that hold no open connection, those inside their reconnect grace
		Task<int> EndIdleSessionsAsync();
	}

	// Holds, renews, takes and hands over the realtime lease of docs/realtime-failover.md and closes this copy's connections when it loses it
	public sealed class FailoverCoordinator
	{
		private static readonly ILogger s_log = AppLog.For(typeof(FailoverCoordinator));

		private readonly FailoverSettings m_settings;
		private readonly ILeaseStore m_store;
		private readonly IConnectionDrain m_drain;
		private readonly Func<long> m_clock;
		private readonly SemaphoreSlim m_lock = new(1, 1);

		private int m_role = (int)FailoverRole.Standby;
		private long m_epoch;
		private long m_deadline;
		private long m_lastRenewStart;
		private long m_lastStoreWarning = long.MinValue;
		private int m_handoverRequested;
		private Task m_closing = Task.CompletedTask;
		private Task<bool>? m_handover;

		public FailoverCoordinator(FailoverSettings settings, string copyID, ILeaseStore store, IConnectionDrain drain, Func<long>? clockMs = null)
		{
			m_settings = settings;
			CopyID = copyID;
			m_store = store;
			m_drain = drain;
			m_clock = clockMs ?? (() => Environment.TickCount64);
		}

		public string CopyID { get; }
		public FailoverRole Role => (FailoverRole)Volatile.Read(ref m_role);
		public long Epoch => Interlocked.Read(ref m_epoch);

		// The connections closed after a lost lease, tests wait on it
		internal Task Closing => Volatile.Read(ref m_closing);

		private bool HoldsLease => m_clock() < Interlocked.Read(ref m_deadline);

		// Ready on the holder until its own deadline and during the overlap until the target took over, the deadline makes a stalled loop stop serving too
		public bool IsReady => Role switch
		{
			FailoverRole.Active => HoldsLease,
			FailoverRole.HandingOver => true,
			_ => false
		};

		// A copy that must not act: a standby, a stopped copy, or a holder past its own deadline. The handover overlap still serves its connections.
		public bool IsFenced => Role switch
		{
			FailoverRole.Active => !HoldsLease,
			FailoverRole.HandingOver or FailoverRole.Draining => false,
			_ => true
		};

		private void SetRole(FailoverRole role) => Volatile.Write(ref m_role, (int)role);

		// Linked to the caller and cancelled after timeoutMs
		private static CancellationTokenSource Bounded(long timeoutMs, CancellationToken cancellationToken = default)
		{
			CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			source.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)));
			return source;
		}

		// One lease step: the holder renews and looks for an operator handover, a standby announces itself and takes an expired or handed lease
		public async Task TickAsync(CancellationToken cancellationToken = default)
		{
			await m_lock.WaitAsync(cancellationToken);
			try
			{
				switch (Role)
				{
					case FailoverRole.Active:
						await RenewStepAsync(cancellationToken);
						break;
					case FailoverRole.Standby:
						await StandbyStepAsync(cancellationToken);
						break;
				}
			}
			finally
			{
				m_lock.Release();
			}
		}

		// Runs apart from the loop, so a renewal that hangs on the database still fences the copy at its deadline
		public void CheckDeadline()
		{
			if (Role == FailoverRole.Active && !HoldsLease)
			{
				Lose("no renewal succeeded before the lease deadline");
			}
		}

		private async Task RenewStepAsync(CancellationToken cancellationToken)
		{
			long start = m_clock();
			long deadline = Interlocked.Read(ref m_deadline);
			if (start >= deadline)
			{
				Lose("the lease expired before a renewal");
				return;
			}

			bool renewed;
			try
			{
				using CancellationTokenSource timeout = Bounded(deadline - start, cancellationToken);
				renewed = await m_store.RenewAsync(CopyID, Epoch, m_settings.LeaseMs, timeout.Token);
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				if (m_clock() >= deadline)
				{
					Lose("the database did not answer before the lease expired");
				}
				else
				{
					WarnStore(ex, "Renewing the realtime lease failed, retrying until it expires");
				}
				return;
			}

			if (!renewed)
			{
				Lose("another copy holds the lease with a newer epoch");
				return;
			}

			// a gap of three intervals means the loop or the database stalled
			long gap = start - m_lastRenewStart;
			if (gap > m_settings.RenewIntervalMs * 3L)
			{
				s_log.LogWarning("Realtime lease of copy {CopyId} renewed late, {GapMs} ms after the previous renewal", CopyID, gap);
				AppMetrics.RecordFailoverLease("renewed_late");
			}
			m_lastRenewStart = start;
			if (Role == FailoverRole.Active)
			{
				Interlocked.Exchange(ref m_deadline, start + m_settings.LeaseMs);
			}

			// one interval at most, so the next renewal starts in time
			try
			{
				using CancellationTokenSource timeout = Bounded(m_settings.RenewIntervalMs, cancellationToken);
				RealtimeLease? lease = await m_store.ReadAsync(timeout.Token);
				if (lease?.HandoverRequestedAt != null && lease.Holder == CopyID)
				{
					Interlocked.Exchange(ref m_handoverRequested, 1);
				}
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				WarnStore(ex, "Reading the realtime lease failed");
			}
		}

		private async Task StandbyStepAsync(CancellationToken cancellationToken)
		{
			try
			{
				// a lease handed to this copy is confirmed with a renewal, which also starts its own deadline
				RealtimeLease? lease;
				using (CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken))
				{
					lease = await m_store.ReadAsync(timeout.Token);
				}
				if (lease != null && lease.Holder == CopyID)
				{
					long start = m_clock();
					using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken);
					if (await m_store.RenewAsync(CopyID, lease.Epoch, m_settings.LeaseMs, timeout.Token))
					{
						BecomeActive(lease.Epoch, start, "handed over");
					}
					return;
				}

				using (CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken))
				{
					await m_store.AnnounceStandbyAsync(CopyID, m_settings.LeaseMs, timeout.Token);
				}

				long takeStart = m_clock();
				long? epoch;
				using (CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken))
				{
					epoch = await m_store.TryTakeAsync(CopyID, m_settings.LeaseMs, timeout.Token);
				}
				if (epoch != null)
				{
					BecomeActive(epoch.Value, takeStart, "taken after expiry");
				}
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				WarnStore(ex, "The realtime lease could not be checked");
			}
		}

		private void BecomeActive(long epoch, long start, string how)
		{
			Interlocked.Exchange(ref m_epoch, epoch);
			Interlocked.Exchange(ref m_deadline, start + m_settings.LeaseMs);
			m_lastRenewStart = start;
			SetRole(FailoverRole.Active);
			s_log.LogInformation("Realtime lease acquired by copy {CopyId} with epoch {Epoch} ({How})", CopyID, epoch, how);
			AppMetrics.RecordFailoverLease("acquired");
		}

		// Stops serving at once and closes every connection, so a paused or cut off copy never acts next to the new active copy
		private void Lose(string reason)
		{
			// the loop and the deadline check may both get here, only one closes
			if (Interlocked.CompareExchange(ref m_role, (int)FailoverRole.Standby, (int)FailoverRole.Active) != (int)FailoverRole.Active)
			{
				return;
			}
			Interlocked.Exchange(ref m_deadline, 0);
			s_log.LogWarning("Realtime lease lost by copy {CopyId} with epoch {Epoch}: {Reason}. Closing every connection with {CloseCode}.", CopyID, Epoch, reason, CloseCodes.ServerRestarting);
			AppMetrics.RecordFailoverLease("lost");
			Volatile.Write(ref m_closing, CloseAllObservedAsync(CloseCodes.ServerRestarting));
		}

		private async Task CloseAllObservedAsync(string closeCode)
		{
			try
			{
				await Task.Yield();
				int closed = await m_drain.CloseAllAsync(closeCode);
				s_log.LogInformation("Copy {CopyId} closed {Count} sessions with {CloseCode}", CopyID, closed, closeCode);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Closing the connections of copy {CopyId} failed", CopyID);
			}
		}

		// One store warning per lease period, the loop retries every interval
		private void WarnStore(Exception ex, string message)
		{
			long now = m_clock();
			if (now - m_lastStoreWarning >= m_settings.LeaseMs)
			{
				m_lastStoreWarning = now;
				s_log.LogWarning(ex, "{Message} on copy {CopyId}", message, CopyID);
			}
		}

		// Takes and consumes an operator handover request seen by the last renewal
		internal bool TakeHandoverRequest() => Interlocked.Exchange(ref m_handoverRequested, 0) == 1;

		// The lease loop with its deadline check, an operator handover that ends on the standby asks the host to stop
		public async Task RunAsync(Action stopApplication, CancellationToken stopping)
		{
			s_log.LogInformation("Realtime failover on: copy {CopyId}, renewal every {IntervalMs} ms, lease {LeaseMs} ms", CopyID, m_settings.RenewIntervalMs, m_settings.LeaseMs);
			int period = Math.Max(10, Math.Min(100, m_settings.RenewIntervalMs / 2));
			using Timer watchdog = new(_ => CheckDeadline(), null, period, period);
			while (!stopping.IsCancellationRequested && Role != FailoverRole.Stopped)
			{
				try
				{
					await TickAsync(stopping);
					if (TakeHandoverRequest())
					{
						if (await HandOverAsync(closeWithoutStandby: false))
						{
							s_log.LogInformation("Copy {CopyId} handed over on the operator command and stops", CopyID);
							stopApplication();
							return;
						}
					}
					await Task.Delay(m_settings.RenewIntervalMs, stopping);
				}
				catch (OperationCanceledException) when (stopping.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Realtime lease loop step failed on copy {CopyId}", CopyID);
					await Task.Delay(m_settings.RenewIntervalMs, CancellationToken.None);
				}
			}
		}

		// The shutdown signal: a running operator drain finishes first, within the handover budget, then the copy hands over or closes
		public async Task ShutdownAsync()
		{
			Task<bool>? running = Volatile.Read(ref m_handover);
			if (running != null && !running.IsCompleted)
			{
				s_log.LogInformation("Copy {CopyId} waits for its running handover before it stops", CopyID);
				await Task.WhenAny(running, Task.Delay(m_settings.HandoverBudgetMs));
			}
			if (await HandOverAsync(closeWithoutStandby: true))
			{
				return;
			}

			// a target that never confirmed gave the lease back, so the stopping copy still closes and releases it
			long epoch;
			await m_lock.WaitAsync();
			try
			{
				if (Role != FailoverRole.Active)
				{
					return;
				}
				epoch = Epoch;
				SetRole(FailoverRole.Draining);
			}
			finally
			{
				m_lock.Release();
			}
			await CloseWithoutStandbyAsync(epoch, m_drain.OpenConnections);
		}

		// The planned switch: B becomes ready first, then this copy reports not ready and switches its connections over in batches.
		// Without a live standby a shutdown closes every connection with server.restarting, an operator request changes nothing.
		// A target that never confirms, or whose lease lapses during the drain, cancels the switch and this copy takes the lease back.
		// True when the lease went to a standby.
		public async Task<bool> HandOverAsync(bool closeWithoutStandby)
		{
			Database.Lease.Handover? handover;
			long epoch;
			int connections = m_drain.OpenConnections;
			TaskCompletionSource<bool> done;

			await m_lock.WaitAsync();
			try
			{
				if (Role != FailoverRole.Active)
				{
					// a shutting down standby must not take the lease on its way out
					if (closeWithoutStandby && Role == FailoverRole.Standby)
					{
						SetRole(FailoverRole.Stopped);
					}
					return false;
				}

				epoch = Epoch;
				try
				{
					using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs);
					handover = await m_store.HandOverAsync(CopyID, epoch, m_settings.LeaseMs, timeout.Token);
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Handing the realtime lease over failed on copy {CopyId}", CopyID);
					handover = null;
				}

				if (handover == null && !closeWithoutStandby)
				{
					s_log.LogWarning("Handover requested on copy {CopyId} but no live standby waits, the copy keeps serving", CopyID);
					await IgnoreStoreErrors(token => m_store.ClearHandoverRequestAsync(CopyID, epoch, token));
					return false;
				}

				SetRole(handover == null ? FailoverRole.Draining : FailoverRole.HandingOver);
				done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				Volatile.Write(ref m_handover, done.Task);
			}
			finally
			{
				m_lock.Release();
			}

			bool result = false;
			try
			{
				result = handover == null ? await CloseWithoutStandbyAsync(epoch, connections) : await SwitchToAsync(handover, connections);
				return result;
			}
			finally
			{
				done.TrySetResult(result);
			}
		}

		private async Task<bool> CloseWithoutStandbyAsync(long epoch, int connections)
		{
			s_log.LogWarning("Copy {CopyId} stops without a live standby, closing {Count} connections with {CloseCode}", CopyID, connections, CloseCodes.ServerRestarting);
			await m_drain.CloseAllAsync(CloseCodes.ServerRestarting);
			await IgnoreStoreErrors(token => m_store.ReleaseAsync(CopyID, epoch, token));
			SetRole(FailoverRole.Stopped);
			return false;
		}

		private async Task<bool> SwitchToAsync(Database.Lease.Handover handover, int connections)
		{
			long started = m_clock();
			s_log.LogInformation("Handover from copy {CopyId} to {Target} with epoch {Epoch} started, {Count} connections", CopyID, handover.Target, handover.Epoch, connections);
			AppMetrics.RecordFailoverLease("handed_over");

			if (!await WaitForTargetAsync(handover))
			{
				// a target that renewed meanwhile makes the take back fail, and the switch goes on
				if (await TakeBackAsync(handover, "the target did not confirm"))
				{
					return false;
				}
				if (Role != FailoverRole.HandingOver)
				{
					return false;
				}
			}

			// not ready from here on, the proxy health check needs a moment to send new connections to the target
			SetRole(FailoverRole.Draining);
			await Task.Delay(m_settings.SwitchDelayMs);
			if (await TakeBackAsync(handover, "the target lease lapsed"))
			{
				return false;
			}

			int batch = Math.Max(1, (int)Math.Ceiling(Math.Max(connections, m_drain.OpenConnections) * m_settings.BatchPercent / 100.0));
			long limit = started + m_settings.HandoverLimitS * 1000L;
			int moved = 0;
			while (true)
			{
				bool late = m_clock() >= limit;
				int switched = await m_drain.SwitchAsync(late ? int.MaxValue : batch, NextRetryAfterMs);
				moved += switched;
				if (switched > 0)
				{
					AppMetrics.RecordFailoverConnectionsMoved(switched);
				}
				if (switched == 0 || late)
				{
					break;
				}
				await Task.Delay(m_settings.BatchIntervalMs);
				if (await TakeBackAsync(handover, "the target lease lapsed"))
				{
					return false;
				}
			}

			// connections that opened during the last batch get ServerSwitching too, then the sessions inside their reconnect grace end
			moved += await m_drain.SwitchAsync(int.MaxValue, NextRetryAfterMs);
			await m_drain.EndIdleSessionsAsync();

			long elapsed = m_clock() - started;
			SetRole(FailoverRole.Stopped);
			AppMetrics.RecordFailoverHandover(TimeSpan.FromMilliseconds(elapsed));
			s_log.LogInformation("Handover from copy {CopyId} to {Target} ended after {ElapsedMs} ms, {Moved} connections switched", CopyID, handover.Target, elapsed, moved);
			return true;
		}

		// The target confirms with its own renewal, false when it did not within one lease period
		private async Task<bool> WaitForTargetAsync(Database.Lease.Handover handover)
		{
			long until = m_clock() + m_settings.LeaseMs;
			while (m_clock() < until)
			{
				try
				{
					using CancellationTokenSource timeout = Bounded(m_settings.RenewIntervalMs);
					RealtimeLease? lease = await m_store.ReadAsync(timeout.Token);
					if (lease != null && lease.Holder == handover.Target && lease.Epoch == handover.Epoch && lease.ExpiresAt > handover.ExpiresAt)
					{
						return true;
					}
				}
				catch (Exception ex)
				{
					WarnStore(ex, "Reading the realtime lease during the handover failed");
				}
				await Task.Delay(Math.Min(100, m_settings.RenewIntervalMs));
			}
			return false;
		}

		// Takes the lease back from a target that never renewed it or whose lease lapsed, with the next epoch, and serves the open connections again.
		// A database error leaves the outcome unknown, so the copy then stops serving and closes everything.
		private async Task<bool> TakeBackAsync(Database.Lease.Handover handover, string why)
		{
			long start = m_clock();
			long? epoch;
			try
			{
				using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs);
				epoch = await m_store.ReclaimAsync(CopyID, handover, m_settings.LeaseMs, timeout.Token);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Copy {CopyId} could not check the handover target {Target}, closing every connection with {CloseCode}", CopyID, handover.Target, CloseCodes.ServerRestarting);
				SetRole(FailoverRole.Standby);
				Volatile.Write(ref m_closing, CloseAllObservedAsync(CloseCodes.ServerRestarting));
				return true;
			}

			if (epoch == null)
			{
				return false;
			}

			s_log.LogWarning("Handover from copy {CopyId} to {Target} cancelled: {Why}. The copy took the lease back and serves its open connections again.", CopyID, handover.Target, why);
			AppMetrics.RecordFailoverLease("handover_cancelled");
			BecomeActive(epoch.Value, start, "taken back from " + handover.Target);
			return true;
		}

		private int NextRetryAfterMs() => Random.Shared.Next(m_settings.SwitchRetryMinMs, m_settings.SwitchRetryMaxMs + 1);

		private async Task IgnoreStoreErrors(Func<CancellationToken, Task> action)
		{
			try
			{
				using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs);
				await action(timeout.Token);
			}
			catch (Exception ex)
			{
				s_log.LogWarning(ex, "A realtime lease update failed on copy {CopyId}", CopyID);
			}
		}
	}
}
