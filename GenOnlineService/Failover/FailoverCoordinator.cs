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
		Target,
		Stopped
	}

	// How a copy became the lease holder: a first take finds no holder it watched, after expiry takes over from a holder that failed
	public enum FailoverActivation
	{
		FirstTake,
		HandedOver,
		AfterExpiry,
		TakenBack
	}

	// The lease statements of Database.Lease, behind an interface so the coordinator runs without a database in tests
	public interface ILeaseStore
	{
		Task<RealtimeLease?> ReadAsync(CancellationToken cancellationToken);
		Task<long?> TryTakeAsync(string copyID, int leaseMs, CancellationToken cancellationToken);
		Task<bool> RenewAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken);
		Task AnnounceStandbyAsync(string copyID, int leaseMs, CancellationToken cancellationToken);
		Task<string?> BeginHandoverAsync(string copyID, long epoch, CancellationToken cancellationToken);
		Task<bool> ConfirmHandoverAsync(string copyID, CancellationToken cancellationToken);
		Task<bool> TargetAliveAsync(string copyID, long epoch, string target, CancellationToken cancellationToken);
		Task<long?> CancelHandoverAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken);
		Task<Database.Lease.Handover?> CompleteHandoverAsync(string copyID, long epoch, int leaseMs, CancellationToken cancellationToken);
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

	// The live state steps of a planned handover between the overlap and the batches, build step 4
	public interface IHandoverMover
	{
		// Moves the queue, then the lobbies one by one when quiet or at the limit, false when the target stopped answering
		Task<bool> MoveAsync(string target, long limitAtMs, Func<long> clockMs, Func<Task<bool>> targetAlive);

		// Reads what the target owns before a cancel, the take back restores it
		Task PrepareTakeBackAsync(string target);
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
		private long m_targetDeadline;
		private long m_lastRenewStart;
		private long m_lastStoreWarning = long.MinValue;
		private int m_handoverRequested;
		private Task m_closing = Task.CompletedTask;
		private Task<bool>? m_handover;

		// Set once a take failed because another copy held the lease, so a later take follows a failure of that holder
		private bool m_watchedHolder;

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

		// Runs before a copy that took the lease reports ready, build step 3 restores the live state here
		public Action<FailoverActivation, long>? Activating { get; set; }

		// A standby announces itself only while this holds, build step 3 makes it the in sync check of the live state
		public Func<bool> StandbyReady { get; set; } = () => true;

		// Runs when the copy stops owning anything, before its connections close, so nothing of it is finalized next to the new owner
		public Action? Fencing { get; set; }

		// The live state moves of a planned handover, none without live state
		public IHandoverMover? Mover { get; set; }

		// The connections closed after a lost lease, tests wait on it
		internal Task Closing => Volatile.Read(ref m_closing);

		private bool HoldsLease => m_clock() < Interlocked.Read(ref m_deadline);

		private bool TargetValid => m_clock() < Interlocked.Read(ref m_targetDeadline);

		private static bool IsHolderRole(FailoverRole role) => role is FailoverRole.Active or FailoverRole.HandingOver or FailoverRole.Draining;

		// Ready on the holder until its own deadline, also while the handover waits for its target, and on the target during the overlap
		public bool IsReady => Role switch
		{
			FailoverRole.Active or FailoverRole.HandingOver => HoldsLease,
			FailoverRole.Target => TargetValid,
			_ => false
		};

		// A copy that must not act: a standby, a stopped copy, a holder past its own deadline, or a target that lost sight of the lease row
		public bool IsFenced => Role switch
		{
			FailoverRole.Active or FailoverRole.HandingOver or FailoverRole.Draining => !HoldsLease,
			FailoverRole.Target => !TargetValid,
			_ => true
		};

		// True while this copy may write the live state it owns
		public bool OwnsState => (Role is FailoverRole.Active or FailoverRole.HandingOver or FailoverRole.Draining or FailoverRole.Target) && !IsFenced;

		private void SetRole(FailoverRole role) => Volatile.Write(ref m_role, (int)role);

		// Linked to the caller and cancelled after timeoutMs
		private static CancellationTokenSource Bounded(long timeoutMs, CancellationToken cancellationToken = default)
		{
			CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			source.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)));
			return source;
		}

		// One lease step: the holder renews, also during its handover, a standby announces itself and takes an expired lease, a target follows the handover
		public async Task TickAsync(CancellationToken cancellationToken = default)
		{
			await m_lock.WaitAsync(cancellationToken);
			try
			{
				switch (Role)
				{
					case FailoverRole.Active:
					case FailoverRole.HandingOver:
					case FailoverRole.Draining:
						await RenewStepAsync(cancellationToken);
						break;
					case FailoverRole.Standby:
						await StandbyStepAsync(cancellationToken);
						break;
					case FailoverRole.Target:
						await TargetStepAsync(cancellationToken);
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
			FailoverRole role = Role;
			if (IsHolderRole(role) && !HoldsLease)
			{
				Lose("no renewal succeeded before the lease deadline");
			}
			else if (role == FailoverRole.Target && !TargetValid)
			{
				StepDown("the lease row could not be read during the handover");
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
			if (IsHolderRole(Role))
			{
				Interlocked.Exchange(ref m_deadline, start + m_settings.LeaseMs);
			}
			if (Role != FailoverRole.Active)
			{
				return;
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
				RealtimeLease? lease;
				long start = m_clock();
				using (CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken))
				{
					lease = await m_store.ReadAsync(timeout.Token);
				}

				// a lease handed to this copy is confirmed with a renewal, which also starts its own deadline
				if (lease != null && lease.Holder == CopyID)
				{
					using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken);
					if (await m_store.RenewAsync(CopyID, lease.Epoch, m_settings.LeaseMs, timeout.Token))
					{
						BecomeActive(lease.Epoch, start, "handed over", FailoverActivation.HandedOver);
					}
					return;
				}

				// the holder starts a handover to this copy: it confirms and serves next to the holder until the lease moves
				if (lease != null && lease.HandoverTarget == CopyID)
				{
					using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken);
					if (await m_store.ConfirmHandoverAsync(CopyID, timeout.Token))
					{
						Interlocked.Exchange(ref m_targetDeadline, start + m_settings.LeaseMs);
						m_watchedHolder = true;
						SetRole(FailoverRole.Target);
						s_log.LogInformation("Copy {CopyId} is the handover target of {Holder} and serves", CopyID, lease.Holder);
						AppMetrics.RecordFailoverLease("target");
					}
					return;
				}

				// a standby that is not ready lets its entry lapse, so no handover goes to it
				if (StandbyReady())
				{
					using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken);
					await m_store.AnnounceStandbyAsync(CopyID, m_settings.LeaseMs, timeout.Token);
				}

				await TryTakeAsync(cancellationToken);
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				WarnStore(ex, "The realtime lease could not be checked");
			}
		}

		// The target renews its standby entry so the holder sees it alive, takes the lease when the holder hands it over or dies, and steps down on a cancel
		private async Task TargetStepAsync(CancellationToken cancellationToken)
		{
			try
			{
				long start = m_clock();
				RealtimeLease? lease;
				using (CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken))
				{
					lease = await m_store.ReadAsync(timeout.Token);
				}

				if (lease != null && lease.Holder == CopyID)
				{
					using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken);
					if (await m_store.RenewAsync(CopyID, lease.Epoch, m_settings.LeaseMs, timeout.Token))
					{
						BecomeActive(lease.Epoch, start, "handed over", FailoverActivation.HandedOver);
					}
					return;
				}

				if (lease == null || lease.HandoverTarget != CopyID)
				{
					StepDown("the holder cancelled the handover");
					return;
				}

				Interlocked.Exchange(ref m_targetDeadline, start + m_settings.LeaseMs);
				using (CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken))
				{
					await m_store.AnnounceStandbyAsync(CopyID, m_settings.LeaseMs, timeout.Token);
				}

				// a holder that dies during the handover lets its lease expire, the target then owns everything
				await TryTakeAsync(cancellationToken);
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				WarnStore(ex, "The realtime lease could not be checked during the handover");
			}
		}

		private async Task TryTakeAsync(CancellationToken cancellationToken)
		{
			long takeStart = m_clock();
			long? epoch;
			using (CancellationTokenSource timeout = Bounded(m_settings.LeaseMs, cancellationToken))
			{
				epoch = await m_store.TryTakeAsync(CopyID, m_settings.LeaseMs, timeout.Token);
			}
			if (epoch != null)
			{
				BecomeActive(epoch.Value, takeStart, m_watchedHolder ? "taken after expiry" : "first take", m_watchedHolder ? FailoverActivation.AfterExpiry : FailoverActivation.FirstTake);
			}
			else
			{
				m_watchedHolder = true;
			}
		}

		private void BecomeActive(long epoch, long start, string how, FailoverActivation activation)
		{
			Interlocked.Exchange(ref m_epoch, epoch);
			Interlocked.Exchange(ref m_deadline, start + m_settings.LeaseMs);
			m_lastRenewStart = start;
			m_watchedHolder = false;
			try
			{
				Activating?.Invoke(activation, epoch);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Preparing copy {CopyId} to serve with epoch {Epoch} failed, it serves without the restored state", CopyID, epoch);
			}
			SetRole(FailoverRole.Active);
			s_log.LogInformation("Realtime lease acquired by copy {CopyId} with epoch {Epoch} ({How})", CopyID, epoch, how);
			AppMetrics.RecordFailoverLease("acquired");
		}

		private void RunFencing()
		{
			try
			{
				Fencing?.Invoke();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Dropping the state of copy {CopyId} failed", CopyID);
			}
		}

		// Stops serving at once and closes every connection, so a paused or cut off copy never acts next to the new active copy
		private void Lose(string reason)
		{
			// the loop and the deadline check may both get here, only one closes
			while (true)
			{
				int role = Volatile.Read(ref m_role);
				if (!IsHolderRole((FailoverRole)role))
				{
					return;
				}
				if (Interlocked.CompareExchange(ref m_role, (int)FailoverRole.Standby, role) == role)
				{
					break;
				}
			}
			Interlocked.Exchange(ref m_deadline, 0);
			s_log.LogWarning("Realtime lease lost by copy {CopyId} with epoch {Epoch}: {Reason}. Closing every connection with {CloseCode}.", CopyID, Epoch, reason, CloseCodes.ServerRestarting);
			AppMetrics.RecordFailoverLease("lost");
			RunFencing();
			Volatile.Write(ref m_closing, CloseAllObservedAsync(CloseCodes.ServerRestarting));
		}

		// A target whose handover was cancelled or that lost sight of the lease row stops serving, its players go back to the holder
		private void StepDown(string reason)
		{
			if (Interlocked.CompareExchange(ref m_role, (int)FailoverRole.Standby, (int)FailoverRole.Target) != (int)FailoverRole.Target)
			{
				return;
			}
			Interlocked.Exchange(ref m_targetDeadline, 0);
			s_log.LogWarning("Copy {CopyId} stops serving as handover target: {Reason}. Closing every connection with {CloseCode}.", CopyID, reason, CloseCodes.ServerRestarting);
			AppMetrics.RecordFailoverLease("target_stepped_down");
			RunFencing();
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

		// The lease loop with its deadline check, it keeps renewing while an operator handover runs, which asks the host to stop when it ends on the target
		public async Task RunAsync(Action stopApplication, CancellationToken stopping)
		{
			s_log.LogInformation("Realtime failover on: copy {CopyId}, renewal every {IntervalMs} ms, lease {LeaseMs} ms", CopyID, m_settings.RenewIntervalMs, m_settings.LeaseMs);
			int period = Math.Max(10, Math.Min(100, m_settings.RenewIntervalMs / 2));
			using Timer watchdog = new(_ => CheckDeadline(), null, period, period);
			Task<bool>? operatorHandover = null;
			while (!stopping.IsCancellationRequested && Role != FailoverRole.Stopped)
			{
				try
				{
					await TickAsync(stopping);
					if (operatorHandover == null && TakeHandoverRequest())
					{
						operatorHandover = HandOverAsync(closeWithoutStandby: false);
					}
					if (operatorHandover?.IsCompleted == true && !operatorHandover.Result)
					{
						operatorHandover = null;
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

			if (operatorHandover != null && await operatorHandover)
			{
				s_log.LogInformation("Copy {CopyId} handed over on the operator command and stops", CopyID);
				stopApplication();
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

			// a cancelled handover leaves this copy the holder, so the stopping copy still closes and releases the lease
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

		// The planned handover: the lease stays here until the target confirmed, the live state moved and every connection switched, true when it went to the target.
		// Without a live standby a shutdown closes everything with server.restarting, a target that stops answering cancels it with the next epoch.
		public async Task<bool> HandOverAsync(bool closeWithoutStandby)
		{
			string? target;
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
					target = await m_store.BeginHandoverAsync(CopyID, epoch, timeout.Token);
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Starting the handover failed on copy {CopyId}", CopyID);
					target = null;
				}

				if (target == null && !closeWithoutStandby)
				{
					s_log.LogWarning("Handover requested on copy {CopyId} but no live standby waits, the copy keeps serving", CopyID);
					await IgnoreStoreErrors(token => m_store.ClearHandoverRequestAsync(CopyID, epoch, token));
					return false;
				}

				SetRole(target == null ? FailoverRole.Draining : FailoverRole.HandingOver);
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
				result = target == null ? await CloseWithoutStandbyAsync(epoch, connections) : await RunHandoverAsync(target, connections);
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

		private bool Draining => Role == FailoverRole.Draining;

		private async Task<bool> RunHandoverAsync(string target, int connections)
		{
			long started = m_clock();
			s_log.LogInformation("Handover from copy {CopyId} to {Target} started with epoch {Epoch}, {Count} connections", CopyID, target, Epoch, connections);

			if (!await WaitForTargetAsync(target))
			{
				return await CancelAsync(target, "the target did not confirm");
			}
			if (Role != FailoverRole.HandingOver)
			{
				return false;
			}

			// not ready from here on, the proxy health check needs a moment to send new connections to the target
			SetRole(FailoverRole.Draining);
			await Task.Delay(m_settings.SwitchDelayMs);
			long limit = started + m_settings.HandoverLimitS * 1000L;

			// the queue and the lobbies move before anyone else, so a lobby's members always move together
			if (Mover != null && Draining)
			{
				bool liveMoved;
				try
				{
					liveMoved = await Mover.MoveAsync(target, limit, m_clock, () => TargetAliveAsync(target));
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Moving the live state from copy {CopyId} to {Target} failed", CopyID, target);
					liveMoved = false;
				}
				if (!Draining)
				{
					return false;
				}
				if (!liveMoved)
				{
					return await CancelAsync(target, "the target stopped taking over the live state");
				}
			}
			else if (!await TargetAliveAsync(target))
			{
				return await CancelAsync(target, "the target stopped renewing");
			}

			int batch = Math.Max(1, (int)Math.Ceiling(Math.Max(connections, m_drain.OpenConnections) * m_settings.BatchPercent / 100.0));
			int moved = 0;
			while (Draining)
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
				if (!await TargetAliveAsync(target))
				{
					return await CancelAsync(target, "the target stopped renewing");
				}
			}
			if (!Draining)
			{
				return false;
			}

			// connections that opened during the last batch get ServerSwitching too, then the sessions inside their reconnect grace end
			moved += await m_drain.SwitchAsync(int.MaxValue, NextRetryAfterMs);
			await m_drain.EndIdleSessionsAsync();

			// this copy holds no connection and owns nothing, the lease moves last
			Database.Lease.Handover? handover;
			await m_lock.WaitAsync();
			try
			{
				if (!Draining)
				{
					return false;
				}
				try
				{
					using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs);
					handover = await m_store.CompleteHandoverAsync(CopyID, Epoch, m_settings.LeaseMs, timeout.Token);
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Handing the realtime lease to {Target} failed on copy {CopyId}", target, CopyID);
					handover = null;
				}
				if (handover != null)
				{
					SetRole(FailoverRole.Stopped);
				}
			}
			finally
			{
				m_lock.Release();
			}
			if (handover == null)
			{
				return await CancelAsync(target, "the target was gone when the lease was to move");
			}

			long elapsed = m_clock() - started;
			AppMetrics.RecordFailoverLease("handed_over");
			AppMetrics.RecordFailoverHandover(TimeSpan.FromMilliseconds(elapsed));
			s_log.LogInformation("Handover from copy {CopyId} to {Target} ended after {ElapsedMs} ms, {Moved} connections switched, the lease has epoch {Epoch}", CopyID, target, elapsed, moved, handover.Epoch);
			return true;
		}

		// The target confirms within one lease period
		private async Task<bool> WaitForTargetAsync(string target)
		{
			long until = m_clock() + m_settings.LeaseMs;
			while (m_clock() < until && Role == FailoverRole.HandingOver)
			{
				try
				{
					using CancellationTokenSource timeout = Bounded(m_settings.RenewIntervalMs);
					RealtimeLease? lease = await m_store.ReadAsync(timeout.Token);
					if (lease != null && lease.Holder == CopyID && lease.HandoverTarget == target && lease.HandoverConfirmedAt != null)
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

		// A database error counts as alive, a lasting one fences this copy through its own renewal
		private async Task<bool> TargetAliveAsync(string target)
		{
			try
			{
				using CancellationTokenSource timeout = Bounded(m_settings.RenewIntervalMs);
				return await m_store.TargetAliveAsync(CopyID, Epoch, target, timeout.Token);
			}
			catch (Exception ex)
			{
				WarnStore(ex, "Checking the handover target failed");
				return true;
			}
		}

		// Ends the handover with the next epoch and serves again with what the target owned, a database error closes everything since the outcome is unknown
		private async Task<bool> CancelAsync(string target, string why)
		{
			if (Role is not (FailoverRole.HandingOver or FailoverRole.Draining))
			{
				return false;
			}
			if (Mover != null)
			{
				try
				{
					await Mover.PrepareTakeBackAsync(target);
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Reading what the handover target {Target} owns failed on copy {CopyId}", target, CopyID);
				}
			}

			await m_lock.WaitAsync();
			try
			{
				if (Role is not (FailoverRole.HandingOver or FailoverRole.Draining))
				{
					return false;
				}
				long start = m_clock();
				long? epoch;
				try
				{
					using CancellationTokenSource timeout = Bounded(m_settings.LeaseMs);
					epoch = await m_store.CancelHandoverAsync(CopyID, Epoch, m_settings.LeaseMs, timeout.Token);
				}
				catch (Exception ex)
				{
					s_log.LogError(ex, "Copy {CopyId} could not cancel the handover to {Target}, closing every connection with {CloseCode}", CopyID, target, CloseCodes.ServerRestarting);
					SetRole(FailoverRole.Standby);
					Interlocked.Exchange(ref m_deadline, 0);
					RunFencing();
					Volatile.Write(ref m_closing, CloseAllObservedAsync(CloseCodes.ServerRestarting));
					return false;
				}

				if (epoch == null)
				{
					Lose("the lease moved during the handover");
					return false;
				}

				s_log.LogWarning("Handover from copy {CopyId} to {Target} cancelled: {Why}. The copy takes back what the target owned and serves again.", CopyID, target, why);
				AppMetrics.RecordFailoverLease("handover_cancelled");
				BecomeActive(epoch.Value, start, "taken back from " + target, FailoverActivation.TakenBack);
				return false;
			}
			finally
			{
				m_lock.Release();
			}
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
