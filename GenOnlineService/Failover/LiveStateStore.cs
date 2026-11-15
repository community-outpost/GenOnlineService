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
using System.Globalization;

namespace GenOnlineService.Failover
{
	// One change the writer sends: a set with the envelope, a delete, or the reset a new owner starts with
	public sealed record LiveWrite(LiveOp Op, string Key, long Seq, long Version, string? Body);

	// The live state in Redis, behind an interface so the writer and the follower run without Redis in tests
	public interface ILiveStateStore
	{
		// Sends the writes of one owner in order as one pipelined batch
		Task WriteAsync(string copy, long epoch, IReadOnlyList<LiveWrite> writes);

		// Every stored entity with the last stream entry and the owner, read in one transaction
		Task<LiveSnapshot> LoadAsync();

		// Up to count entries after the position, oldest first
		Task<IReadOnlyList<LiveStreamEntry>> ReadAfterAsync(string position, int count);
	}

	// The state hash, the capped change stream and the owner key of one environment
	public sealed class RedisLiveStateStore : ILiveStateStore
	{
		private readonly Func<Task<IConnectionMultiplexer>> m_connect;
		private readonly int m_streamMaxLength;
		private readonly RedisKey m_state;
		private readonly RedisKey m_stream;
		private readonly RedisKey m_owner;

		public RedisLiveStateStore(Func<Task<IConnectionMultiplexer>> connect, string prefix, int streamMaxLength)
		{
			m_connect = connect;
			m_streamMaxLength = streamMaxLength;
			string root = (prefix.Length > 0 ? prefix + ":" : String.Empty) + "genonline:live:";
			m_state = root + "state";
			m_stream = root + "stream";
			m_owner = root + "owner";
		}

		private async Task<IDatabase> Database() => (await m_connect()).GetDatabase();

		public async Task WriteAsync(string copy, long epoch, IReadOnlyList<LiveWrite> writes)
		{
			IDatabase db = await Database();
			IBatch batch = db.CreateBatch();
			List<Task> sent = new(writes.Count * 2 + 1);
			string epochText = epoch.ToString(CultureInfo.InvariantCulture);
			foreach (LiveWrite write in writes)
			{
				switch (write.Op)
				{
					case LiveOp.Reset:
						sent.Add(batch.KeyDeleteAsync(m_state));
						sent.Add(batch.StringSetAsync(m_owner, copy + "|" + epochText));
						break;
					case LiveOp.Delete:
						sent.Add(batch.HashDeleteAsync(m_state, write.Key));
						break;
					case LiveOp.Set:
						sent.Add(batch.HashSetAsync(m_state, write.Key, write.Body));
						break;
				}

				List<NameValueEntry> fields = new(7)
				{
					new("op", OpName(write.Op)),
					new("key", write.Key),
					new("copy", copy),
					new("epoch", epochText),
					new("seq", write.Seq.ToString(CultureInfo.InvariantCulture)),
					new("version", write.Version.ToString(CultureInfo.InvariantCulture))
				};
				if (write.Body != null)
				{
					fields.Add(new("body", write.Body));
				}
				sent.Add(batch.StreamAddAsync(m_stream, fields.ToArray(), maxLength: m_streamMaxLength, useApproximateMaxLength: true));
			}
			batch.Execute();
			await Task.WhenAll(sent);
		}

		public async Task<LiveSnapshot> LoadAsync()
		{
			IDatabase db = await Database();
			ITransaction transaction = db.CreateTransaction();
			Task<HashEntry[]> state = transaction.HashGetAllAsync(m_state);
			Task<StreamEntry[]> last = transaction.StreamRangeAsync(m_stream, "-", "+", 1, Order.Descending);
			Task<RedisValue> owner = transaction.StringGetAsync(m_owner);
			if (!await transaction.ExecuteAsync())
			{
				throw new InvalidOperationException("The live state transaction did not run.");
			}

			Dictionary<string, string> entities = new(StringComparer.Ordinal);
			foreach (HashEntry entry in await state)
			{
				entities[entry.Name.ToString()] = entry.Value.ToString();
			}

			StreamEntry[] lastEntries = await last;
			LiveStreamEntry? lastEntry = lastEntries.Length > 0 ? Parse(lastEntries[0]) : null;

			string? ownerCopy = null;
			long ownerEpoch = 0;
			RedisValue ownerValue = await owner;
			if (ownerValue.HasValue)
			{
				string text = ownerValue.ToString();
				int split = text.LastIndexOf('|');
				if (split > 0 && Int64.TryParse(text.AsSpan(split + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
				{
					ownerCopy = text.Substring(0, split);
					ownerEpoch = parsed;
				}
			}

			return new LiveSnapshot(entities, lastEntry, ownerCopy, ownerEpoch);
		}

		public async Task<IReadOnlyList<LiveStreamEntry>> ReadAfterAsync(string position, int count)
		{
			IDatabase db = await Database();
			StreamEntry[] entries = await db.StreamReadAsync(m_stream, position, count);
			List<LiveStreamEntry> parsed = new(entries.Length);
			foreach (StreamEntry entry in entries)
			{
				parsed.Add(Parse(entry));
			}
			return parsed;
		}

		private static string OpName(LiveOp op) => op switch
		{
			LiveOp.Reset => "reset",
			LiveOp.Delete => "del",
			_ => "set"
		};

		// An unknown op of a later version reads as a set without a body, which changes nothing but keeps the sequence
		private static LiveStreamEntry Parse(StreamEntry entry)
		{
			string Field(string name) => entry[name].HasValue ? entry[name].ToString() : String.Empty;
			long Number(string name) => Int64.TryParse(Field(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

			LiveOp op = Field("op") switch
			{
				"reset" => LiveOp.Reset,
				"del" => LiveOp.Delete,
				_ => LiveOp.Set
			};
			RedisValue body = entry["body"];
			return new LiveStreamEntry(entry.Id.ToString(), op, Field("key"), Field("copy"), Number("epoch"), Number("seq"), Number("version"), body.HasValue ? body.ToString() : null);
		}
	}
}
