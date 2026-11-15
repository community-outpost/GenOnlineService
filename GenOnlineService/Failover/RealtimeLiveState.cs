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

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GenOnlineService.Failover
{
	// The owner side: queues each entity's state and sends it from its own loop as compared writes, an entity another copy took is dropped and reported
	public sealed class LiveStateWriter
	{
		private static readonly ILogger s_log = AppLog.For(typeof(LiveStateWriter));

		// Delete markers older than this are forgotten, a fenced copy stops long before
		public const long TombstoneMs = 600_000;

		private enum Kind { Set, Delete, Transfer }

		private sealed record Change(Kind Kind, string Key, string? Data, string? Target, TaskCompletionSource<bool>? Done);

		private sealed record Claim(LiveClaim Mode, string? Owner, IReadOnlyList<string> Ensure, TaskCompletionSource<IReadOnlyDictionary<string, long>> Done);

		private readonly ILiveStateStore m_store;
		private readonly string m_copy;
		private readonly Func<bool> m_canWrite;
		private readonly Func<long> m_clock;
		private readonly Channel<Change> m_queue = Channel.CreateUnbounded<Change>(new UnboundedChannelOptions { SingleReader = true });
		private readonly ConcurrentDictionary<string, long> m_epochs = new(StringComparer.Ordinal);
		private readonly ConcurrentDictionary<string, byte> m_released = new(StringComparer.Ordinal);
		private readonly Dictionary<string, long> m_versions = new(StringComparer.Ordinal);
		private readonly List<Change> m_carry = new();

		private Claim? m_claim;
		private int m_generation;
		private long m_lastWarning = long.MinValue;
		private long m_nextPrune;
		private bool m_failed;

		public LiveStateWriter(ILiveStateStore store, string copy, Func<bool> canWrite, Func<long>? clockMs = null)
		{
			m_store = store;
			m_copy = copy;
			m_canWrite = canWrite;
			m_clock = clockMs ?? (() => Environment.TickCount64);
		}

		public string Copy => m_copy;

		// True while this copy may write the entities it owns
		public bool Writing => m_canWrite();

		// Raised when every entity has to be written again: writes dropped while not owning, or a failed batch
		public int Generation => Volatile.Read(ref m_generation);

		// Raised on the writer's loop for an entity whose write Redis refused, another copy owns it now
		public event Action<string>? Rejected;

		public bool Owns(string key) => m_epochs.ContainsKey(key);

		public long EpochOf(string key) => m_epochs.TryGetValue(key, out long epoch) ? epoch : 0;

		public void Set(string key, string dataJson) => m_queue.Writer.TryWrite(new Change(Kind.Set, key, dataJson, null, null));

		public void Delete(string key) => m_queue.Writer.TryWrite(new Change(Kind.Delete, key, null, null, null));

		// Gives an owned entity with its final state to another copy at the next owner epoch, true once Redis accepted it
		public Task<bool> TransferAsync(string key, string dataJson, string target)
		{
			TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
			m_queue.Writer.TryWrite(new Change(Kind.Transfer, key, dataJson, target, done));
			return done.Task;
		}

		// Takes ownership before any later write, sent once the copy may write
		public Task<IReadOnlyDictionary<string, long>> ClaimAsync(LiveClaim mode, string? owner, IReadOnlyList<string> ensure)
		{
			TaskCompletionSource<IReadOnlyDictionary<string, long>> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Interlocked.Exchange(ref m_claim, new Claim(mode, owner, ensure, done))?.Done.TrySetCanceled();
			m_queue.Writer.TryWrite(new Change(Kind.Set, String.Empty, null, null, null));
			return done.Task;
		}

		// An entity another copy handed to this one, at the epoch the transfer gave it
		public void Adopt(string key, long epoch)
		{
			m_released.TryRemove(key, out _);
			m_epochs[key] = epoch;
		}

		private int m_releases;

		// A fenced copy owns nothing any more, also not what a batch in flight writes
		public void ReleaseAll()
		{
			Interlocked.Increment(ref m_releases);
			foreach (string key in m_epochs.Keys)
			{
				m_released[key] = 0;
			}
			m_epochs.Clear();
		}

		private void Invalidate() => Interlocked.Increment(ref m_generation);

		private void Warn(Exception ex, string message)
		{
			long now = m_clock();
			if (now - m_lastWarning >= 5000)
			{
				m_lastWarning = now;
				s_log.LogWarning(ex, "{Message} on copy {CopyId}", message, m_copy);
			}
		}

		// Wakes on every queued change and every 250 ms, a failed batch waits a second so an outage costs one write of every entity per second at most
		public async Task RunAsync(CancellationToken stopping)
		{
			while (!stopping.IsCancellationRequested)
			{
				try
				{
					using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(stopping);
					wait.CancelAfter(250);
					await m_queue.Reader.WaitToReadAsync(wait.Token);
				}
				catch (OperationCanceledException)
				{
					if (stopping.IsCancellationRequested)
					{
						return;
					}
				}
				await FlushAsync();
				await PruneAsync();
				if (m_failed)
				{
					try
					{
						await Task.Delay(1000, stopping);
					}
					catch (OperationCanceledException)
					{
						return;
					}
				}
			}
		}

		private async Task PruneAsync()
		{
			long now = m_clock();
			if (now < m_nextPrune || !Writing)
			{
				return;
			}
			m_nextPrune = now + TombstoneMs;
			try
			{
				await m_store.PruneAsync(TombstoneMs);
			}
			catch (Exception ex)
			{
				Warn(ex, "Pruning the live state delete markers failed");
			}
		}

		// Sends a pending claim, then everything queued as one batch with the latest change per entity, a transfer closing its entity. Tests call it directly.
		public async Task<int> FlushAsync()
		{
			List<Change> drained = new(m_carry);
			m_carry.Clear();
			while (m_queue.Reader.TryRead(out Change? change))
			{
				if (change.Key.Length > 0)
				{
					drained.Add(change);
				}
			}

			if (!Writing)
			{
				// a standby or a fenced copy writes nothing, and writes everything again once it owns the state
				foreach (Change change in drained)
				{
					change.Done?.TrySetResult(false);
				}
				if (drained.Any(change => change.Kind != Kind.Transfer))
				{
					Invalidate();
				}
				return 0;
			}

			Claim? claim = Volatile.Read(ref m_claim);
			if (claim != null)
			{
				try
				{
					IReadOnlyDictionary<string, long> claimed = await m_store.ClaimAsync(m_copy, claim.Mode, claim.Owner, claim.Ensure);
					foreach ((string key, long epoch) in claimed)
					{
						Adopt(key, epoch);
					}
					Interlocked.CompareExchange(ref m_claim, null, claim);
					claim.Done.TrySetResult(claimed);
					m_failed = false;
				}
				catch (Exception ex)
				{
					m_carry.AddRange(drained);
					m_failed = true;
					Warn(ex, "Claiming the live state failed");
					return 0;
				}
			}

			// the latest set or delete per entity, a transfer replaces what came before it and later changes of that entity stay out
			List<Change> slots = new();
			Dictionary<string, int> open = new(StringComparer.Ordinal);
			foreach (Change change in drained)
			{
				if (open.TryGetValue(change.Key, out int index))
				{
					if (slots[index].Done != null)
					{
						slots.Add(change);
						continue;
					}
					slots[index] = change;
					if (change.Kind == Kind.Transfer)
					{
						open.Remove(change.Key);
					}
				}
				else
				{
					if (change.Kind != Kind.Transfer)
					{
						open[change.Key] = slots.Count;
					}
					slots.Add(change);
				}
			}

			List<(Change Change, LiveWrite Write)> writes = new(slots.Count);
			HashSet<string> leaving = new(StringComparer.Ordinal);
			foreach (Change change in slots)
			{
				bool owned = m_epochs.TryGetValue(change.Key, out long epoch);
				if (leaving.Contains(change.Key) || m_released.ContainsKey(change.Key) || (!owned && !LiveKeys.IsNew(change.Key, change.Kind == Kind.Transfer)))
				{
					change.Done?.TrySetResult(false);
					continue;
				}

				// a lobby this copy writes for the first time starts at epoch 1
				if (!owned)
				{
					epoch = 1;
				}
				long version = m_versions.TryGetValue(change.Key, out long previous) ? previous + 1 : 1;
				m_versions[change.Key] = version;
				LiveWrite write = change.Kind switch
				{
					Kind.Delete => new LiveWrite(LiveOp.Delete, change.Key, epoch, null, version, null),
					Kind.Transfer => new LiveWrite(LiveOp.Transfer, change.Key, epoch, change.Target, version,
						LiveJson.Envelope(change.Key, change.Target!, epoch + 1, version, change.Data!, m_copy)),
					_ => new LiveWrite(LiveOp.Set, change.Key, epoch, null, version, LiveJson.Envelope(change.Key, m_copy, epoch, version, change.Data!, m_copy))
				};
				writes.Add((change, write));
				if (change.Kind != Kind.Set)
				{
					leaving.Add(change.Key);
				}
			}
			if (writes.Count == 0)
			{
				return 0;
			}

			IReadOnlyList<LiveWriteResult> results;
			int releases = Volatile.Read(ref m_releases);
			try
			{
				results = await m_store.WriteAsync(m_copy, writes.Select(entry => entry.Write).ToList());
				m_failed = false;
			}
			catch (Exception ex)
			{
				// a lost delete would leave its entity stored, so deletes go out again, sets come back with the next generation
				m_failed = true;
				foreach ((Change change, LiveWrite _) in writes)
				{
					if (change.Kind == Kind.Delete)
					{
						m_carry.Add(change);
					}
					change.Done?.TrySetResult(false);
				}
				Invalidate();
				Warn(ex, "Writing the live state to Redis failed");
				AppMetrics.RecordFailoverLiveState("write_failed");
				return 0;
			}

			for (int i = 0; i < writes.Count; i++)
			{
				(Change change, LiveWrite write) = writes[i];
				LiveWriteResult result = results[i];
				if (result.Accepted)
				{
					// a copy that released everything meanwhile does not own the entity again
					if (change.Kind == Kind.Set && !m_released.ContainsKey(change.Key) && Volatile.Read(ref m_releases) == releases)
					{
						m_epochs[change.Key] = result.Epoch;
					}
					else if (change.Kind == Kind.Set)
					{
						m_released[change.Key] = 0;
					}
					else
					{
						m_epochs.TryRemove(change.Key, out _);
						m_released[change.Key] = 0;
						m_versions.Remove(change.Key);
					}
					change.Done?.TrySetResult(true);
					continue;
				}

				// another copy holds a newer owner epoch, or the entity is deleted: this copy drops it
				m_epochs.TryRemove(change.Key, out _);
				m_released[change.Key] = 0;
				if (change.Kind == Kind.Delete)
				{
					continue;
				}
				AppMetrics.RecordFailoverLiveState("write_refused");
				s_log.LogWarning("Copy {CopyId} lost {Key} to a newer owner epoch {Epoch}", m_copy, change.Key, result.Epoch);
				if (change.Done != null)
				{
					change.Done.TrySetResult(false);
				}
				else
				{
					try
					{
						Rejected?.Invoke(change.Key);
					}
					catch (Exception ex)
					{
						s_log.LogError(ex, "Dropping {Key} on copy {CopyId} failed", change.Key, m_copy);
					}
				}
			}
			return writes.Count;
		}
	}

	// The standby side: loads the stored states, follows the stream, and is in sync while caught up and recently read
	public sealed class LiveStateFollower
	{
		private static readonly ILogger s_log = AppLog.For(typeof(LiveStateFollower));

		public const int BatchSize = 500;

		private readonly ILiveStateStore m_store;
		private readonly string m_copy;
		private readonly Func<bool> m_shouldFollow;
		private readonly Func<long> m_clock;
		private readonly int m_pollIntervalMs;
		private readonly int m_staleMs;
		private readonly LiveStateMirror m_mirror = new();
		private readonly object m_lock = new();

		private bool m_inSync;
		private long m_lastRead = long.MinValue;
		private long m_lastInSync = long.MinValue;
		private long m_lastWarning = long.MinValue;

		// Raised outside the lock for each applied write that names this copy as the owner, a handover gives the entity to it
		public event Action<string, string, long>? Owned;

		public LiveStateFollower(ILiveStateStore store, string copy, Func<bool> shouldFollow, int pollIntervalMs, int staleMs, Func<long>? clockMs = null)
		{
			m_store = store;
			m_copy = copy;
			m_shouldFollow = shouldFollow;
			m_pollIntervalMs = pollIntervalMs;
			m_staleMs = staleMs;
			m_clock = clockMs ?? (() => Environment.TickCount64);
		}

		public bool InSync
		{
			get
			{
				lock (m_lock)
				{
					return m_inSync && m_clock() - m_lastRead <= m_staleMs;
				}
			}
		}

		internal LiveStateMirror Mirror => m_mirror;

		private void SetInSync(bool inSync)
		{
			if (m_inSync == inSync)
			{
				return;
			}
			m_inSync = inSync;
			if (inSync)
			{
				s_log.LogInformation("Live state follower of copy {CopyId} is in sync at {Position} with {Count} entities", m_copy, m_mirror.Position, m_mirror.Count);
			}
			else
			{
				s_log.LogInformation("Live state follower of copy {CopyId} is not in sync any more", m_copy);
			}
		}

		// Stops following and hands the mirror over, fresh when the follower was in sync within twice the stale bound
		public (IReadOnlyDictionary<string, string> Entities, bool Fresh) Detach()
		{
			lock (m_lock)
			{
				Dictionary<string, string> entities = new(m_mirror.Entities, StringComparer.Ordinal);
				bool fresh = m_lastInSync != long.MinValue && m_clock() - m_lastInSync <= 2L * m_staleMs;
				m_mirror.Unload();
				SetInSync(false);
				return (entities, fresh);
			}
		}

		public async Task RunAsync(CancellationToken stopping)
		{
			while (!stopping.IsCancellationRequested)
			{
				int read = await StepAsync();
				try
				{
					await Task.Delay(read >= BatchSize ? 0 : m_pollIntervalMs, stopping);
				}
				catch (OperationCanceledException)
				{
					return;
				}
			}
		}

		// One read, returns how many entries it read. Tests call it directly.
		public async Task<int> StepAsync()
		{
			if (!m_shouldFollow())
			{
				lock (m_lock)
				{
					if (m_mirror.Loaded)
					{
						m_mirror.Unload();
					}
					SetInSync(false);
				}
				return 0;
			}

			try
			{
				bool loaded;
				string position;
				lock (m_lock)
				{
					loaded = m_mirror.Loaded;
					position = m_mirror.Position;
				}

				if (!loaded)
				{
					LiveSnapshot snapshot = await m_store.LoadAsync();
					lock (m_lock)
					{
						if (!m_shouldFollow())
						{
							return 0;
						}
						m_mirror.Load(snapshot);
						position = m_mirror.Position;
					}
					AppMetrics.RecordFailoverLiveState("loaded");
				}

				IReadOnlyList<LiveStreamEntry> entries = await m_store.ReadAfterAsync(position, BatchSize);
				List<LiveStreamEntry> owned = new();
				lock (m_lock)
				{
					if (!m_shouldFollow() || !m_mirror.Loaded || m_mirror.Position != position)
					{
						return 0;
					}
					AppMetrics.RecordFailoverMirrorLag(entries.Count);
					foreach (LiveStreamEntry entry in entries)
					{
						if (m_mirror.Apply(entry) == MirrorApply.Gap)
						{
							s_log.LogWarning("Live state stream of copy {CopyId} has a gap after {Position}, reloading every state", m_copy, m_mirror.Position);
							AppMetrics.RecordFailoverLiveState("stream_gap");
							m_mirror.Unload();
							SetInSync(false);
							return entries.Count;
						}
						if (entry.Op != LiveOp.Delete && entry.Body != null && entry.Owner == m_copy && entry.Copy != m_copy)
						{
							owned.Add(entry);
						}
					}
					m_lastRead = m_clock();
					SetInSync(entries.Count < BatchSize);
					if (entries.Count < BatchSize)
					{
						m_lastInSync = m_lastRead;
					}
				}
				foreach (LiveStreamEntry entry in owned)
				{
					try
					{
						Owned?.Invoke(entry.Key, entry.Body!, entry.Epoch);
					}
					catch (Exception ex)
					{
						s_log.LogError(ex, "Taking over {Key} on copy {CopyId} failed", entry.Key, m_copy);
					}
				}
				return entries.Count;
			}
			catch (Exception ex)
			{
				lock (m_lock)
				{
					SetInSync(false);
				}
				long now = m_clock();
				if (now - m_lastWarning >= 5000)
				{
					m_lastWarning = now;
					s_log.LogWarning(ex, "Following the live state in Redis failed on copy {CopyId}", m_copy);
				}
				return 0;
			}
		}
	}

	// Build steps 3 and 4 of docs/realtime-failover.md, on only with failover and the Redis backplane, every call is a no op otherwise
	public static class RealtimeLiveState
	{
		private static LiveStateWriter? s_writer;
		private static LiveStateFollower? s_follower;
		private static int s_refreshMs = 2000;
		private static int s_rewriteMs;

		public static bool Enabled => Volatile.Read(ref s_writer) != null;

		public static bool Writing => Volatile.Read(ref s_writer)?.Writing ?? false;

		public static int Generation => Volatile.Read(ref s_writer)?.Generation ?? 0;

		public static int RefreshMs => s_refreshMs;

		// Zero by default, compare on write keeps a fenced copy's late write out
		public static int RewriteMs => s_rewriteMs;

		internal static LiveStateWriter? Writer => Volatile.Read(ref s_writer);

		// True when the follower applied the stream up to its latest entry, always true without live state
		public static bool StandbyInSync => Volatile.Read(ref s_follower)?.InSync ?? true;

		// True when this copy owns the entity, always true without live state
		public static bool Owns(string key) => Volatile.Read(ref s_writer)?.Owns(key) ?? true;

		internal static void Install(LiveStateWriter? writer, LiveStateFollower? follower, int refreshMs, int rewriteMs)
		{
			s_refreshMs = refreshMs;
			s_rewriteMs = rewriteMs;
			Volatile.Write(ref s_follower, follower);
			Volatile.Write(ref s_writer, writer);
		}

		public static void Set(string key, string dataJson) => Volatile.Read(ref s_writer)?.Set(key, dataJson);

		public static void Delete(string key) => Volatile.Read(ref s_writer)?.Delete(key);

		public static (IReadOnlyDictionary<string, string> Entities, bool Fresh) DetachMirror() =>
			Volatile.Read(ref s_follower)?.Detach() ?? (new Dictionary<string, string>(), false);
	}
}
