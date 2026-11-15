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
	// One change the writer sends: the owner epoch it holds and, for a transfer, the new owner
	public sealed record LiveWrite(LiveOp Op, string Key, long Epoch, string? Target, long Version, string? Body);

	// Accepted with the entity's owner epoch after the change, or refused because another owner or a newer epoch holds it
	public sealed record LiveWriteResult(bool Accepted, long Epoch);

	// Which entities a claim takes: all that another copy owns (a takeover) or those of one owner (a take back)
	public enum LiveClaim
	{
		NotMine,
		OwnedBy
	}

	// The live state in Redis, behind an interface so the writer and the follower run without Redis in tests
	public interface ILiveStateStore
	{
		// Each write is compared with the stored owner epoch, a refused write changes nothing
		Task<IReadOnlyList<LiveWriteResult>> WriteAsync(string copy, IReadOnlyList<LiveWrite> writes);

		// Raises the owner epoch of the matching entities and makes the caller their owner, returns the live ones with their new epoch
		Task<IReadOnlyDictionary<string, long>> ClaimAsync(string copy, LiveClaim mode, string? owner, IReadOnlyList<string> ensure);

		Task<LiveSnapshot> LoadAsync();

		// Up to count entries after the position, oldest first
		Task<IReadOnlyList<LiveStreamEntry>> ReadAfterAsync(string position, int count);

		// One stored envelope, null when there is none
		Task<string?> ReadAsync(string key);

		// Forgets the delete markers older than the age, a fenced copy is long gone by then
		Task PruneAsync(long olderThanMs);
	}

	// The state hash, the owner epochs, the capped change stream and each copy's write count of one environment.
	// Every change runs as one Lua script, which Redis executes without any other command in between, so the compare and the write are one step.
	public sealed class RedisLiveStateStore : ILiveStateStore
	{
		// KEYS state, epochs, stream, seqs. ARGV op, key, writer, epoch, target, version, body, maxlen, now.
		// An entity is written only by its owner at the epoch it holds, an absent one only at epoch 1, a transfer gives it to the target at epoch plus one.
		// The epochs field is "epoch|owner", a deleted entity keeps "epoch|owner|d|ms" and is never written again.
		public const string WriteScript = @"
local cur = redis.call('HGET', KEYS[2], ARGV[2])
local epoch = tonumber(ARGV[4])
if cur then
  local s = string.find(cur, '|', 1, true)
  local ce = tonumber(string.sub(cur, 1, s - 1))
  local rest = string.sub(cur, s + 1)
  local t = string.find(rest, '|', 1, true)
  local co = rest
  if t then return {0, ce} end
  if ce ~= epoch or co ~= ARGV[3] then return {0, ce} end
elseif epoch ~= 1 then
  return {0, 0}
end
local ne = epoch
local no = ARGV[3]
if ARGV[1] == 'transfer' then
  ne = epoch + 1
  no = ARGV[5]
end
if ARGV[1] == 'del' then
  redis.call('HDEL', KEYS[1], ARGV[2])
  redis.call('HSET', KEYS[2], ARGV[2], ne .. '|' .. no .. '|d|' .. ARGV[9])
else
  redis.call('HSET', KEYS[1], ARGV[2], ARGV[7])
  redis.call('HSET', KEYS[2], ARGV[2], ne .. '|' .. no)
end
local seq = redis.call('HINCRBY', KEYS[4], ARGV[3], 1)
redis.call('XADD', KEYS[3], 'MAXLEN', '~', ARGV[8], '*', 'op', ARGV[1], 'key', ARGV[2], 'copy', ARGV[3], 'seq', seq, 'epoch', ne, 'owner', no, 'version', ARGV[6], 'body', ARGV[7])
return {1, ne}
";

		// KEYS epochs. ARGV copy, mode (others or owner), owner, then the keys to own even when absent.
		// The matching entities get the epoch plus one with the caller as owner, deleted ones keep their marker and are not returned.
		public const string ClaimScript = @"
local out = {}
local all = redis.call('HGETALL', KEYS[1])
local seen = {}
for i = 1, #all, 2 do
  local f = all[i]
  local v = all[i + 1]
  seen[f] = true
  local s = string.find(v, '|', 1, true)
  local ce = tonumber(string.sub(v, 1, s - 1))
  local rest = string.sub(v, s + 1)
  local t = string.find(rest, '|', 1, true)
  local co = rest
  local mark = ''
  if t then
    co = string.sub(rest, 1, t - 1)
    mark = string.sub(rest, t)
  end
  local take = (ARGV[2] == 'others' and co ~= ARGV[1]) or (ARGV[2] == 'owner' and co == ARGV[3])
  if take then
    redis.call('HSET', KEYS[1], f, (ce + 1) .. '|' .. ARGV[1] .. mark)
    if mark == '' then
      table.insert(out, f)
      table.insert(out, tostring(ce + 1))
    end
  end
end
for i = 4, #ARGV do
  if not seen[ARGV[i]] then
    redis.call('HSET', KEYS[1], ARGV[i], '1|' .. ARGV[1])
    table.insert(out, ARGV[i])
    table.insert(out, '1')
  end
end
return out
";

		// KEYS epochs. ARGV now, max age. Drops the delete markers older than the age.
		public const string PruneScript = @"
local all = redis.call('HGETALL', KEYS[1])
local removed = 0
for i = 1, #all, 2 do
  local v = all[i + 1]
  local d = string.find(v, '|d|', 1, true)
  if d and tonumber(ARGV[1]) - tonumber(string.sub(v, d + 3)) > tonumber(ARGV[2]) then
    redis.call('HDEL', KEYS[1], all[i])
    removed = removed + 1
  end
end
return removed
";

		private readonly Func<Task<IConnectionMultiplexer>> m_connect;
		private readonly int m_streamMaxLength;
		private readonly RedisKey m_state;
		private readonly RedisKey m_epochs;
		private readonly RedisKey m_stream;
		private readonly RedisKey m_seqs;

		public RedisLiveStateStore(Func<Task<IConnectionMultiplexer>> connect, string prefix, int streamMaxLength)
		{
			m_connect = connect;
			m_streamMaxLength = streamMaxLength;
			string root = (prefix.Length > 0 ? prefix + ":" : String.Empty) + "genonline:live:";
			m_state = root + "state";
			m_epochs = root + "epochs";
			m_stream = root + "stream";
			m_seqs = root + "seqs";
		}

		private async Task<IDatabase> Database() => (await m_connect()).GetDatabase();

		private static string Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

		private static string OpName(LiveOp op) => op switch
		{
			LiveOp.Delete => "del",
			LiveOp.Transfer => "transfer",
			_ => "set"
		};

		public async Task<IReadOnlyList<LiveWriteResult>> WriteAsync(string copy, IReadOnlyList<LiveWrite> writes)
		{
			IDatabase db = await Database();
			IBatch batch = db.CreateBatch();
			RedisKey[] keys = { m_state, m_epochs, m_stream, m_seqs };
			string now = Now();
			List<Task<RedisResult>> sent = new(writes.Count);
			foreach (LiveWrite write in writes)
			{
				RedisValue[] args =
				{
					OpName(write.Op), write.Key, copy, write.Epoch.ToString(CultureInfo.InvariantCulture), write.Target ?? String.Empty,
					write.Version.ToString(CultureInfo.InvariantCulture), write.Body ?? String.Empty, m_streamMaxLength.ToString(CultureInfo.InvariantCulture), now
				};
				sent.Add(batch.ScriptEvaluateAsync(WriteScript, keys, args));
			}
			batch.Execute();
			await Task.WhenAll(sent);

			List<LiveWriteResult> results = new(sent.Count);
			foreach (Task<RedisResult> task in sent)
			{
				RedisResult[] values = (RedisResult[])task.Result!;
				results.Add(new LiveWriteResult((long)values[0] == 1, (long)values[1]));
			}
			return results;
		}

		public async Task<IReadOnlyDictionary<string, long>> ClaimAsync(string copy, LiveClaim mode, string? owner, IReadOnlyList<string> ensure)
		{
			IDatabase db = await Database();
			List<RedisValue> args = new() { copy, mode == LiveClaim.NotMine ? "others" : "owner", owner ?? String.Empty };
			args.AddRange(ensure.Select(key => (RedisValue)key));
			RedisResult[] values = (RedisResult[])(await db.ScriptEvaluateAsync(ClaimScript, new RedisKey[] { m_epochs }, args.ToArray()))!;
			Dictionary<string, long> claimed = new(StringComparer.Ordinal);
			for (int i = 0; i + 1 < values.Length; i += 2)
			{
				claimed[values[i].ToString()!] = Int64.Parse(values[i + 1].ToString()!, CultureInfo.InvariantCulture);
			}
			return claimed;
		}

		public async Task<LiveSnapshot> LoadAsync()
		{
			IDatabase db = await Database();
			ITransaction transaction = db.CreateTransaction();
			Task<HashEntry[]> state = transaction.HashGetAllAsync(m_state);
			Task<HashEntry[]> seqs = transaction.HashGetAllAsync(m_seqs);
			Task<StreamEntry[]> last = transaction.StreamRangeAsync(m_stream, "-", "+", 1, Order.Descending);
			if (!await transaction.ExecuteAsync())
			{
				throw new InvalidOperationException("The live state transaction did not run.");
			}

			Dictionary<string, string> entities = new(StringComparer.Ordinal);
			foreach (HashEntry entry in await state)
			{
				entities[entry.Name.ToString()] = entry.Value.ToString();
			}
			Dictionary<string, long> counts = new(StringComparer.Ordinal);
			foreach (HashEntry entry in await seqs)
			{
				counts[entry.Name.ToString()] = (long)entry.Value;
			}
			StreamEntry[] lastEntries = await last;
			return new LiveSnapshot(entities, counts, lastEntries.Length > 0 ? lastEntries[0].Id.ToString() : "0-0");
		}

		public async Task<IReadOnlyList<LiveStreamEntry>> ReadAfterAsync(string position, int count)
		{
			IDatabase db = await Database();
			StreamEntry[] entries = await db.StreamReadAsync(m_stream, position, count);
			return entries.Select(Parse).ToList();
		}

		public async Task<string?> ReadAsync(string key)
		{
			IDatabase db = await Database();
			RedisValue value = await db.HashGetAsync(m_state, key);
			return value.HasValue ? value.ToString() : null;
		}

		public async Task PruneAsync(long olderThanMs)
		{
			IDatabase db = await Database();
			await db.ScriptEvaluateAsync(PruneScript, new RedisKey[] { m_epochs }, new RedisValue[] { Now(), olderThanMs });
		}

		// An unknown op of a later version reads as a set without a body, which changes nothing but keeps the sequence
		private static LiveStreamEntry Parse(StreamEntry entry)
		{
			string Field(string name) => entry[name].HasValue ? entry[name].ToString() : String.Empty;
			long Number(string name) => Int64.TryParse(Field(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

			LiveOp op = Field("op") switch
			{
				"del" => LiveOp.Delete,
				"transfer" => LiveOp.Transfer,
				_ => LiveOp.Set
			};
			string body = Field("body");
			return new LiveStreamEntry(entry.Id.ToString(), op, Field("key"), Field("copy"), Number("seq"), Number("epoch"), Field("owner"), Number("version"), body.Length > 0 ? body : null);
		}
	}
}
