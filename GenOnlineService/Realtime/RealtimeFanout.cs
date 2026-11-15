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

using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace GenOnlineService.Realtime
{
	// The channel and lobby group members on this copy, mirrored from the group calls of this copy's connections
	public sealed class LocalChatGroups
	{
		private readonly object m_lock = new();
		private readonly Dictionary<string, Dictionary<string, long>> m_groups = new(StringComparer.Ordinal);
		private readonly Dictionary<string, HashSet<string>> m_byConnection = new(StringComparer.Ordinal);

		// Only channel and lobby groups carry chat that is filtered per recipient
		public static bool Tracks(string group) => group.StartsWith("channel:", StringComparison.Ordinal) || group.StartsWith("lobby:", StringComparison.Ordinal);

		public void Add(string connectionID, long userID, string group)
		{
			if (!Tracks(group))
			{
				return;
			}

			lock (m_lock)
			{
				if (!m_groups.TryGetValue(group, out Dictionary<string, long>? members))
				{
					members = new Dictionary<string, long>(StringComparer.Ordinal);
					m_groups[group] = members;
				}
				members[connectionID] = userID;

				if (!m_byConnection.TryGetValue(connectionID, out HashSet<string>? groups))
				{
					groups = new HashSet<string>(StringComparer.Ordinal);
					m_byConnection[connectionID] = groups;
				}
				groups.Add(group);
			}
		}

		public void Remove(string connectionID, string group)
		{
			if (!Tracks(group))
			{
				return;
			}

			lock (m_lock)
			{
				RemoveLocked(connectionID, group);
				if (m_byConnection.TryGetValue(connectionID, out HashSet<string>? groups))
				{
					groups.Remove(group);
					if (groups.Count == 0)
					{
						m_byConnection.Remove(connectionID);
					}
				}
			}
		}

		public void RemoveConnection(string connectionID)
		{
			lock (m_lock)
			{
				if (!m_byConnection.Remove(connectionID, out HashSet<string>? groups))
				{
					return;
				}
				foreach (string group in groups)
				{
					RemoveLocked(connectionID, group);
				}
			}
		}

		private void RemoveLocked(string connectionID, string group)
		{
			if (m_groups.TryGetValue(group, out Dictionary<string, long>? members) && members.Remove(connectionID) && members.Count == 0)
			{
				m_groups.Remove(group);
			}
		}

		// The connections of the group whose user is not skipped, a short skip list is searched in place and a long one through one set
		public string[] Recipients(string group, IReadOnlyCollection<long> skipped)
		{
			Func<long, bool> isSkipped = skipped.Count == 0 ? _ => false
				: skipped.Count <= 16 || skipped is IReadOnlySet<long> ? skipped.Contains
				: new HashSet<long>(skipped).Contains;
			lock (m_lock)
			{
				if (!m_groups.TryGetValue(group, out Dictionary<string, long>? members))
				{
					return Array.Empty<string>();
				}
				return members.Where(member => !isSkipped(member.Value)).Select(member => member.Key).ToArray();
			}
		}
	}

	// One ordered queue of pending chat writes per connection, so a connection that stops reading holds up only itself
	internal sealed class ConnectionOutboxes
	{
		private static readonly ILogger s_log = AppLog.For(typeof(ConnectionOutboxes));

		private sealed class Outbox
		{
			public required Channel<Func<ValueTask>> Queue { get; init; }
			public int Aborted;
		}

		private readonly ConcurrentDictionary<string, Outbox> m_outboxes = new(StringComparer.Ordinal);
		private readonly int m_limit;

		// limit is the number of writes that may wait behind the one in progress
		public ConnectionOutboxes(int limit)
		{
			m_limit = limit;
		}

		public int Count => m_outboxes.Count;

		// Queues a write behind the connection's earlier ones; past the limit the connection is aborted once and takes no more writes
		public bool Enqueue(string connectionID, Func<ValueTask> write, Action abort)
		{
			Outbox outbox = m_outboxes.GetOrAdd(connectionID, Start);
			if (Volatile.Read(ref outbox.Aborted) != 0)
			{
				return false;
			}
			if (outbox.Queue.Writer.TryWrite(write))
			{
				return true;
			}

			if (Interlocked.Exchange(ref outbox.Aborted, 1) == 0)
			{
				outbox.Queue.Writer.TryComplete();
				s_log.LogWarning("Connection {ConnectionId} fell more than {Limit} chat messages behind and is aborted", connectionID, m_limit);
				abort();
			}
			return false;
		}

		// The pending writes of a closed connection are dropped
		public void Remove(string connectionID)
		{
			if (m_outboxes.TryRemove(connectionID, out Outbox? outbox))
			{
				outbox.Queue.Writer.TryComplete();
			}
		}

		private Outbox Start(string connectionID)
		{
			Outbox outbox = new Outbox { Queue = Channel.CreateBounded<Func<ValueTask>>(new BoundedChannelOptions(m_limit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }) };
			_ = Task.Run(() => PumpAsync(connectionID, outbox.Queue.Reader));
			return outbox;
		}

		private static async Task PumpAsync(string connectionID, ChannelReader<Func<ValueTask>> reader)
		{
			await foreach (Func<ValueTask> write in reader.ReadAllAsync())
			{
				try
				{
					await write();
				}
				catch (Exception ex)
				{
					s_log.LogDebug(ex, "A chat write to connection {ConnectionId} failed", connectionID);
				}
			}
		}
	}

	// One chat message on its way to every copy: the group, the users blocked either way with the author, the event
	internal sealed record ChatFanoutEnvelope
	{
		public string Group { get; init; } = String.Empty;
		public long[] Skipped { get; init; } = Array.Empty<long>();
		public Message Message { get; init; } = new Message();
	}

	// Carries channel and lobby chat on one Redis channel, every copy including the sender queues the writes in publish order
	internal sealed class ChatFanout
	{
		private static readonly ILogger s_log = AppLog.For(typeof(ChatFanout));
		private static readonly JsonSerializerOptions s_json = RealtimeJson.Create();

		private readonly ISubscriber m_subscriber;
		private readonly RedisChannel m_channel;
		private readonly Func<ChatFanoutEnvelope, Task> m_deliver;

		private ChatFanout(ISubscriber subscriber, RedisChannel channel, Func<ChatFanoutEnvelope, Task> deliver)
		{
			m_subscriber = subscriber;
			m_channel = channel;
			m_deliver = deliver;
		}

		// The queue runs one message at a time and delivery only queues the writes, so every connection gets them in publish order
		public static async Task<ChatFanout> StartAsync(ISubscriber subscriber, RedisChannel channel, Func<ChatFanoutEnvelope, Task> deliver)
		{
			ChatFanout fanout = new ChatFanout(subscriber, channel, deliver);
			ChannelMessageQueue queue = await subscriber.SubscribeAsync(channel);
			queue.OnMessage(message => fanout.HandleAsync(message.Message));
			return fanout;
		}

		public static string Serialize(ChatFanoutEnvelope envelope) => JsonSerializer.Serialize(envelope, s_json);
		public static ChatFanoutEnvelope? Deserialize(string payload) => JsonSerializer.Deserialize<ChatFanoutEnvelope>(payload, s_json);

		// A failed publish throws as a failed group send through the backplane does
		public Task PublishAsync(ChatFanoutEnvelope envelope) => m_subscriber.PublishAsync(m_channel, Serialize(envelope));

		private async Task HandleAsync(RedisValue payload)
		{
			try
			{
				ChatFanoutEnvelope? envelope = Deserialize(payload.ToString());
				if (envelope != null)
				{
					await m_deliver(envelope);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "Delivering a chat message from the fan-out failed");
			}
		}
	}

	// Channel and lobby chat with the Redis backplane: each copy filters its own recipients, so a block counts on every copy
	public static class RealtimeFanout
	{
		private static readonly RedisChannel s_channel = RedisChannel.Literal("realtime.chat");
		private static ChatFanout? s_fanout;

		// Chat writes that may wait behind the one in progress before the connection is aborted, a client that far behind reconnects and reads the history
		public const int OutboxLimit = 256;

		internal static LocalChatGroups Groups { get; } = new LocalChatGroups();
		internal static ConnectionOutboxes Outboxes { get; } = new ConnectionOutboxes(OutboxLimit);

		// True when chat goes through the fan-out instead of a SignalR group send
		public static bool IsShared => Volatile.Read(ref s_fanout) != null;

		internal static async Task StartAsync(ISubscriber subscriber)
		{
			ChatFanout fanout = await ChatFanout.StartAsync(subscriber, s_channel,
				envelope =>
				{
					RealtimeLifetimeManager.WriteLocal(Groups, Outboxes, envelope.Group, nameof(IRealtimeClient.MessageCreate), envelope.Message, envelope.Skipped);
					return Task.CompletedTask;
				});
			Volatile.Write(ref s_fanout, fanout);
		}

		// Sends a MessageCreate to every copy's members of the group except the skipped users
		public static Task SendAsync(string group, Message message, long[] skipped)
		{
			ChatFanout fanout = Volatile.Read(ref s_fanout) ?? throw new InvalidOperationException("The chat fan-out runs only with the Redis backplane.");
			return fanout.PublishAsync(new ChatFanoutEnvelope { Group = group, Skipped = skipped, Message = message });
		}
	}
}
