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

using System.Threading.Channels;

namespace GenOnlineService.Failover
{
	// The owner side: queues each entity's state and sends it from its own loop, a new own epoch starts with a reset entry
	public sealed class LiveStateWriter
	{
		private static readonly ILogger s_log = AppLog.For(typeof(LiveStateWriter));

		private readonly ILiveStateStore m_store;
		private readonly string m_copy;
		private readonly Func<long?> m_activeEpoch;
		private readonly Func<long> m_clock;
		private readonly Channel<(LiveOp Op, string Key, string? Data)> m_queue =
			Channel.CreateUnbounded<(LiveOp, string, string?)>(new UnboundedChannelOptions { SingleReader = true });
		private readonly Dictionary<string, long> m_versions = new(StringComparer.Ordinal);

		private long m_epoch;
		private long m_seq;
		private int m_generation;
		private long m_lastWarning = long.MinValue;
		private bool m_failed;

		public LiveStateWriter(ILiveStateStore store, string copy, Func<long?> activeEpoch, Func<long>? clockMs = null)
		{
			m_store = store;
			m_copy = copy;
			m_activeEpoch = activeEpoch;
			m_clock = clockMs ?? (() => Environment.TickCount64);
		}

		// True while this copy owns the live state, publishers skip the work otherwise
		public bool Writing => m_activeEpoch() != null;

		// Raised when every entity has to be written again: a new own epoch, writes dropped while not owning, or a failed batch
		public int Generation => Volatile.Read(ref m_generation);

		public void Set(string key, string dataJson) => m_queue.Writer.TryWrite((LiveOp.Set, key, dataJson));

		public void Delete(string key) => m_queue.Writer.TryWrite((LiveOp.Delete, key, null));

		private int m_resetPending;

		private void Invalidate() => Interlocked.Increment(ref m_generation);

		// A copy that becomes the owner starts with a reset, even when it has nothing to write, so the hash holds no older owner's entities
		public void RequestReset()
		{
			Volatile.Write(ref m_resetPending, 1);
			m_queue.Writer.TryWrite((LiveOp.Reset, "*", null));
		}

		// Wakes on every queued change and every 250 ms, so a reset requested before the role changed is still sent
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

				// a failed batch waits a second, so a Redis outage costs one write of every entity per second at most
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

		// Sends everything queued as one batch, the latest change per entity only. Tests call it directly.
		public async Task<int> FlushAsync()
		{
			List<string> order = new();
			Dictionary<string, (LiveOp Op, string? Data)> latest = new(StringComparer.Ordinal);
			while (m_queue.Reader.TryRead(out (LiveOp Op, string Key, string? Data) change))
			{
				if (change.Op == LiveOp.Reset)
				{
					continue;
				}
				if (!latest.ContainsKey(change.Key))
				{
					order.Add(change.Key);
				}
				latest[change.Key] = (change.Op, change.Data);
			}
			bool reset = Volatile.Read(ref m_resetPending) == 1;
			if (order.Count == 0 && !reset)
			{
				return 0;
			}

			long? epoch = m_activeEpoch();
			if (epoch == null)
			{
				// a standby or a fenced copy writes nothing, and writes everything again once it owns the state
				if (order.Count > 0)
				{
					Invalidate();
				}
				return 0;
			}

			List<LiveWrite> writes = new(order.Count + 1);
			if (epoch.Value != m_epoch || reset)
			{
				// a new epoch starts its sequence over, a reset within the same epoch continues it
				if (epoch.Value != m_epoch)
				{
					m_epoch = epoch.Value;
					m_seq = 0;
				}
				Volatile.Write(ref m_resetPending, 0);
				writes.Add(new LiveWrite(LiveOp.Reset, "*", ++m_seq, 0, null));
				Invalidate();
			}

			foreach (string key in order)
			{
				(LiveOp op, string? data) = latest[key];
				long version = m_versions.TryGetValue(key, out long previous) ? previous + 1 : 1;
				if (op == LiveOp.Delete)
				{
					m_versions.Remove(key);
					writes.Add(new LiveWrite(LiveOp.Delete, key, ++m_seq, version, null));
				}
				else
				{
					m_versions[key] = version;
					writes.Add(new LiveWrite(LiveOp.Set, key, ++m_seq, version, LiveJson.Envelope(key, m_copy, m_epoch, version, data!)));
				}
			}

			try
			{
				await m_store.WriteAsync(m_copy, m_epoch, writes);
				m_failed = false;
			}
			catch (Exception ex)
			{
				m_failed = true;

				// the sequence numbers are spent and a lost delete would leave its entity in the hash, so the next batch starts with a reset
				Volatile.Write(ref m_resetPending, 1);
				Invalidate();
				long now = m_clock();
				if (now - m_lastWarning >= 5000)
				{
					m_lastWarning = now;
					s_log.LogWarning(ex, "Writing the live state of copy {CopyId} to Redis failed", m_copy);
				}
				AppMetrics.RecordFailoverLiveState("write_failed");
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
					}
					m_lastRead = m_clock();
					SetInSync(entries.Count < BatchSize);
					if (entries.Count < BatchSize)
					{
						m_lastInSync = m_lastRead;
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

	// Build step 3 of docs/realtime-failover.md, on only with failover and the Redis backplane, every call is a no op otherwise
	public static class RealtimeLiveState
	{
		private static LiveStateWriter? s_writer;
		private static LiveStateFollower? s_follower;
		private static int s_refreshMs = 2000;
		private static int s_rewriteMs = 30000;

		public static bool Enabled => Volatile.Read(ref s_writer) != null;

		public static bool Writing => Volatile.Read(ref s_writer)?.Writing ?? false;

		public static int Generation => Volatile.Read(ref s_writer)?.Generation ?? 0;

		public static int RefreshMs => s_refreshMs;

		public static int RewriteMs => s_rewriteMs;

		// True when the follower applied the stream up to its latest entry, always true without live state
		public static bool StandbyInSync => Volatile.Read(ref s_follower)?.InSync ?? true;

		internal static void Install(LiveStateWriter? writer, LiveStateFollower? follower, int refreshMs, int rewriteMs)
		{
			s_refreshMs = refreshMs;
			s_rewriteMs = rewriteMs;
			Volatile.Write(ref s_follower, follower);
			Volatile.Write(ref s_writer, writer);
		}

		public static void Set(string key, string dataJson) => Volatile.Read(ref s_writer)?.Set(key, dataJson);

		public static void Delete(string key) => Volatile.Read(ref s_writer)?.Delete(key);

		// The copy became the owner, its first write is a reset
		public static void Activated() => Volatile.Read(ref s_writer)?.RequestReset();

		public static (IReadOnlyDictionary<string, string> Entities, bool Fresh) DetachMirror() =>
			Volatile.Read(ref s_follower)?.Detach() ?? (new Dictionary<string, string>(), false);
	}
}
