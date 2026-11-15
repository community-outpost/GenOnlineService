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

// The live state layout against a real Redis: the pipelined batch, the transaction of the standby's load and the stream read
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
			await db.KeyDeleteAsync(new RedisKey[] { prefix + ":genonline:live:state", prefix + ":genonline:live:stream", prefix + ":genonline:live:owner" });
			await redis.CloseAsync();
		}
	}

	[ProbeRedisFact]
	public Task TheOwnerWritesAndAStandbyLoadsAndFollowsInOrder() => Probe(async (redis, prefix, store) =>
	{
		LiveStateWriter writer = new(store, "a", () => 4);
		writer.Set("lobby:1", Data("one"));
		writer.Set("queue", Data("q"));
		await writer.FlushAsync();

		LiveStateFollower follower = new(store, "b", () => true, 100, 2500);
		await follower.StepAsync();
		Assert.True(follower.InSync);
		Assert.Equal(new[] { "lobby:1", "queue" }, follower.Mirror.Entities.Keys.OrderBy(k => k).ToArray());
		Assert.Equal(("a", 4L, 3L), (follower.Mirror.WriterCopy, follower.Mirror.WriterEpoch, follower.Mirror.WriterSeq));
		Assert.Equal("a|4", (await redis.GetDatabase().StringGetAsync(prefix + ":genonline:live:owner")).ToString());

		writer.Delete("lobby:1");
		writer.Set("lobby:2", Data("two"));
		await writer.FlushAsync();
		Assert.Equal(2, await follower.StepAsync());
		Assert.Equal(new[] { "lobby:2", "queue" }, follower.Mirror.Entities.Keys.OrderBy(k => k).ToArray());

		LiveEnvelope envelope = LiveJson.ReadEnvelope(follower.Mirror.Entities["lobby:2"])!;
		Assert.Equal(("a", 4L, 1L, "two"), (envelope.Owner, envelope.Epoch, envelope.Version, envelope.Data.GetProperty("value").GetString()));
	});

	[ProbeRedisFact]
	public Task AStreamTrimmedPastTheStandbyMakesItReloadTheHash() => Probe(async (redis, prefix, store) =>
	{
		LiveStateWriter writer = new(store, "a", () => 1);
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
	});

	[ProbeRedisFact]
	public Task ANewOwnerClearsTheHashAndTheStreamCapStaysNearItsLimit() => Probe(async (redis, prefix, store) =>
	{
		LiveStateWriter a = new(store, "a", () => 1);
		for (int i = 0; i < 1500; i++)
		{
			a.Set("lobby:" + (i % 1000), Data("v" + i));
			if (i % 100 == 99)
			{
				await a.FlushAsync();
			}
		}
		await a.FlushAsync();

		LiveStateWriter b = new(store, "b", () => 2);
		b.RequestReset();
		await b.FlushAsync();

		IDatabase db = redis.GetDatabase();
		Assert.Equal(0, await db.HashLengthAsync(prefix + ":genonline:live:state"));
		Assert.Equal("b|2", (await db.StringGetAsync(prefix + ":genonline:live:owner")).ToString());

		// XADD MAXLEN ~ trims in whole nodes, so the stream stays within a node of the cap
		Assert.InRange(await db.StreamLengthAsync(prefix + ":genonline:live:stream"), 1, 1000 + 200);
	});
}
