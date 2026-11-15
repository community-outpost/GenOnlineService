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
using StackExchange.Redis;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// Runs only when GENONLINE_PROBE_REDIS holds a StackExchange.Redis connection string, each test uses its own key prefix and deletes its keys
public sealed class ProbeRedisFactAttribute : FactAttribute
{
	public const string Variable = "GENONLINE_PROBE_REDIS";

	public ProbeRedisFactAttribute()
	{
		if (Environment.GetEnvironmentVariable(Variable) == null)
		{
			Skip = $"Set {Variable} to run the Redis probes";
		}
	}
}

// The live state layout against a real Redis: the Lua compare on write, transfers, claims and delete markers, the transaction of the load and the stream read
public class LiveStateRedisProbeTests
{
	private static string Data(string value) => JsonSerializer.Serialize(new { value }, LiveJson.Options);

	private static async Task Probe(Func<IConnectionMultiplexer, string, RedisLiveStateStore, Task> body)
	{
		IConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable(ProbeRedisFactAttribute.Variable)!);
		string prefix = "probe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
		try
		{
			await body(redis, prefix, new RedisLiveStateStore(() => Task.FromResult(redis), prefix, 1000));
		}
		finally
		{
			IDatabase db = redis.GetDatabase();
			await db.KeyDeleteAsync(new RedisKey[] { prefix + ":genonline:live:state", prefix + ":genonline:live:stream", prefix + ":genonline:live:epochs", prefix + ":genonline:live:seqs" });
			await redis.CloseAsync();
		}
	}

	private static async Task<IReadOnlyDictionary<string, long>> ClaimNow(LiveStateWriter writer, LiveClaim mode, string? owner)
	{
		Task<IReadOnlyDictionary<string, long>> claim = writer.ClaimAsync(mode, owner, new[] { LiveKeys.Queue });
		await writer.FlushAsync();
		return await claim;
	}

	private static string? ValueOf(IReadOnlyDictionary<string, string> entities, string key) =>
		entities.TryGetValue(key, out string? json) ? LiveJson.ReadEnvelope(json)!.Data.GetProperty("value").GetString() : null;

	private static LiveWrite Write(LiveOp op, string key, long epoch, string? body, string? target = null) => new(op, key, epoch, target, 1, body);

	[ProbeRedisFact]
	public Task TheOwnerWritesAndAStandbyLoadsAndFollowsInOrder() => Probe(async (redis, prefix, store) =>
	{
		LiveStateWriter writer = new(store, "a", () => true);
		await ClaimNow(writer, LiveClaim.NotMine, null);
		writer.Set("lobby:1", Data("one"));
		writer.Set(LiveKeys.Queue, Data("q"));
		await writer.FlushAsync();

		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		await follower.StepAsync();
		Assert.True(follower.InSync);
		Assert.Equal(new[] { "lobby:1", "queue" }, follower.Mirror.Entities.Keys.OrderBy(k => k).ToArray());
		Assert.Equal(2L, follower.Mirror.SeqOf("a"));

		writer.Delete("lobby:1");
		writer.Set("lobby:2", Data("two"));
		await writer.FlushAsync();
		Assert.Equal(2, await follower.StepAsync());
		Assert.Equal(new[] { "lobby:2", "queue" }, follower.Mirror.Entities.Keys.OrderBy(k => k).ToArray());

		LiveEnvelope envelope = LiveJson.ReadEnvelope(follower.Mirror.Entities["lobby:2"])!;
		Assert.Equal(("a", 1L, 1L, "two"), (envelope.Owner, envelope.Epoch, envelope.Version, envelope.Data.GetProperty("value").GetString()));
	});

	[ProbeRedisFact]
	public Task TheScriptRefusesAWriteBelowTheStoredOwnerEpochAndAnyWriteOfAnotherOwner() => Probe(async (redis, prefix, store) =>
	{
		IDatabase db = redis.GetDatabase();
		RedisKey epochs = prefix + ":genonline:live:epochs";
		Assert.Equal(new LiveWriteResult(true, 1), (await store.WriteAsync("a", new[] { Write(LiveOp.Set, "lobby:1", 1, "{}") }))[0]);
		Assert.Equal("1|a", (await db.HashGetAsync(epochs, "lobby:1")).ToString());

		// a new entity needs epoch 1, another copy and another epoch are refused, nothing reaches the stream
		IReadOnlyList<LiveWriteResult> refused = await store.WriteAsync("b", new[]
		{
			Write(LiveOp.Set, "lobby:2", 5, "{}"),
			Write(LiveOp.Set, "lobby:1", 1, "{\"x\":1}"),
			Write(LiveOp.Set, "lobby:1", 2, "{\"x\":1}")
		});
		Assert.Equal(new[] { new LiveWriteResult(false, 0), new LiveWriteResult(false, 1), new LiveWriteResult(false, 1) }, refused);
		Assert.Equal(1, await db.StreamLengthAsync(prefix + ":genonline:live:stream"));
		Assert.Equal("{}", await store.ReadAsync("lobby:1"));

		// the owner transfers it, from then on only the target at the next epoch writes it
		Assert.Equal(new LiveWriteResult(true, 2), (await store.WriteAsync("a", new[] { Write(LiveOp.Transfer, "lobby:1", 1, "{\"t\":1}", "b") }))[0]);
		Assert.False((await store.WriteAsync("a", new[] { Write(LiveOp.Set, "lobby:1", 1, "{}") }))[0].Accepted);
		Assert.False((await store.WriteAsync("a", new[] { Write(LiveOp.Set, "lobby:1", 2, "{}") }))[0].Accepted);
		Assert.True((await store.WriteAsync("b", new[] { Write(LiveOp.Set, "lobby:1", 2, "{\"b\":1}") }))[0].Accepted);

		// a delete leaves a marker that keeps the old epoch out, a claim raises it, a prune of old markers forgets it
		Assert.True((await store.WriteAsync("b", new[] { Write(LiveOp.Delete, "lobby:1", 2, null) }))[0].Accepted);
		Assert.Null(await store.ReadAsync("lobby:1"));
		Assert.StartsWith("2|b|d|", (await db.HashGetAsync(epochs, "lobby:1")).ToString());
		Assert.False((await store.WriteAsync("a", new[] { Write(LiveOp.Set, "lobby:1", 1, "{}") }))[0].Accepted);
		IReadOnlyDictionary<string, long> claimed = await store.ClaimAsync("c", LiveClaim.NotMine, null, Array.Empty<string>());
		Assert.False(claimed.ContainsKey("lobby:1"));
		Assert.StartsWith("3|c|d|", (await db.HashGetAsync(epochs, "lobby:1")).ToString());
		await store.PruneAsync(600_000);
		Assert.True(await db.HashExistsAsync(epochs, "lobby:1"));
		await Task.Delay(50);
		await store.PruneAsync(1);
		Assert.False(await db.HashExistsAsync(epochs, "lobby:1"));

		// every accepted write counts for its copy
		LiveSnapshot snapshot = await store.LoadAsync();
		Assert.Equal((2L, 2L), (snapshot.Seqs["a"], snapshot.Seqs["b"]));
	});

	[ProbeRedisFact]
	public Task AClaimTakesOneOwnerOrEveryOtherOwnerAndEnsuresTheQueue() => Probe(async (redis, prefix, store) =>
	{
		await store.WriteAsync("a", new[] { Write(LiveOp.Set, "lobby:1", 1, "{}") });
		await store.WriteAsync("b", new[] { Write(LiveOp.Set, "lobby:2", 1, "{}") });

		IReadOnlyDictionary<string, long> fromB = await store.ClaimAsync("a", LiveClaim.OwnedBy, "b", new[] { LiveKeys.Queue });
		Assert.Equal(new Dictionary<string, long> { ["lobby:2"] = 2, [LiveKeys.Queue] = 1 }, fromB);
		IReadOnlyDictionary<string, long> all = await store.ClaimAsync("c", LiveClaim.NotMine, null, new[] { LiveKeys.Queue });
		Assert.Equal(new Dictionary<string, long> { ["lobby:1"] = 2, ["lobby:2"] = 3, [LiveKeys.Queue] = 2 }, all);
		Assert.True((await store.WriteAsync("c", new[] { Write(LiveOp.Set, "lobby:2", 3, "{}") }))[0].Accepted);
		Assert.False((await store.WriteAsync("a", new[] { Write(LiveOp.Set, "lobby:2", 2, "{}") }))[0].Accepted);
	});

	[ProbeRedisFact]
	public Task ATransferAndAClaimRacingLetOneSeeTheOther() => Probe(async (redis, prefix, store) =>
	{
		for (int round = 0; round < 20; round++)
		{
			string key = "lobby:" + (100 + round);
			await store.WriteAsync("a", new[] { Write(LiveOp.Set, key, 1, "{}") });

			// Redis runs one script after the other, so the transfer either lands before the claim or is refused after it
			Task<IReadOnlyList<LiveWriteResult>> transfer = store.WriteAsync("a", new[] { Write(LiveOp.Transfer, key, 1, "{}", "b") });
			Task<IReadOnlyDictionary<string, long>> claim = store.ClaimAsync("c", LiveClaim.NotMine, null, Array.Empty<string>());
			await Task.WhenAll(transfer, claim);
			string stored = (await redis.GetDatabase().HashGetAsync(prefix + ":genonline:live:epochs", key)).ToString();
			Assert.True((transfer.Result[0].Accepted && stored == "3|c") || (!transfer.Result[0].Accepted && stored == "2|c"), stored);
		}
	});

	[ProbeRedisFact]
	public Task AStreamTrimmedPastTheStandbyMakesItReloadTheHash() => Probe(async (redis, prefix, store) =>
	{
		LiveStateWriter writer = new(store, "a", () => true);
		writer.Set("lobby:1", Data("one"));
		await writer.FlushAsync();
		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		await follower.StepAsync();

		for (int i = 0; i < 4; i++)
		{
			writer.Set("lobby:" + (10 + i), Data("n"));
			await writer.FlushAsync();
		}
		await redis.GetDatabase().StreamTrimAsync(prefix + ":genonline:live:stream", 2, useApproximateMaxLength: false);

		await follower.StepAsync();
		Assert.False(follower.Mirror.Loaded);
		await follower.StepAsync();
		Assert.True(follower.InSync);
		Assert.Equal(new[] { "lobby:1", "lobby:10", "lobby:11", "lobby:12", "lobby:13" }, follower.Mirror.Entities.Keys.OrderBy(k => k).ToArray());
		Assert.Equal("n", ValueOf(follower.Mirror.Entities, "lobby:13"));
	});

	[ProbeRedisFact]
	public Task TheStreamCapStaysNearItsLimit() => Probe(async (redis, prefix, store) =>
	{
		LiveStateWriter a = new(store, "a", () => true);
		for (int i = 0; i < 1500; i++)
		{
			a.Set("lobby:" + (i % 1000), Data("v" + i));
			if (i % 100 == 99)
			{
				await a.FlushAsync();
			}
		}
		await a.FlushAsync();

		// XADD MAXLEN ~ trims in whole nodes, so the stream stays within a node of the cap
		Assert.InRange(await redis.GetDatabase().StreamLengthAsync(prefix + ":genonline:live:stream"), 1, 1000 + 200);
	});
}
