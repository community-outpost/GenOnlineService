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

using Database;
using GenOnlineService.Failover;
using GenOnlineService.Names;
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Reflection;
using Xunit;

namespace GenOnlineService.Tests;

// Block decisions outside chat stay current across copies: cached lists follow every copy's changes, joins and formed matches read the database
public class BlockCacheTests
{
	private static UserSocialContainer Social(long[]? friends = null, long[]? pending = null, long[]? blocked = null) => new UserSocialContainer
	{
		Friends = new HashSet<long>(friends ?? Array.Empty<long>()),
		PendingRequests = new HashSet<long>(pending ?? Array.Empty<long>()),
		Blocked = new HashSet<long>(blocked ?? Array.Empty<long>())
	};

	[Fact]
	public void ABlockMessageFromAnotherCopyUpdatesBothUsersCaches()
	{
		Dictionary<long, UserSocialContainer> online = new()
		{
			[1] = Social(friends: new long[] { 2, 3 }, pending: new long[] { 2 }),
			[2] = Social(friends: new long[] { 1 }, pending: new long[] { 1 })
		};
		BlockCaches copy2 = new BlockCaches(id => online.GetValueOrDefault(id));

		// copy 1 publishes the change, copy 2 reads it off the channel
		string payload = BlockFanout.Serialize(new BlockChange { UserId = 1, OtherId = 2, Blocked = true });
		Assert.Equal("{\"user_id\":1,\"other_id\":2,\"blocked\":true}", payload);
		long before = copy2.Sequence;
		copy2.Apply(BlockFanout.Deserialize(payload)!);

		Assert.True(copy2.HasBlocked(1, 2));
		Assert.Equal(new long[] { 3 }, online[1].Friends);
		Assert.Empty(online[1].PendingRequests);
		Assert.Empty(online[2].Friends);
		Assert.Empty(online[2].PendingRequests);
		Assert.Empty(online[2].Blocked);
		Assert.Equal(before + 1, copy2.Sequence);

		// an unblock lifts only the block, the friendship the block ended stays ended
		copy2.Apply(BlockFanout.Deserialize(BlockFanout.Serialize(new BlockChange { UserId = 1, OtherId = 2, Blocked = false }))!);
		Assert.False(copy2.HasBlocked(1, 2));
		Assert.Equal(new long[] { 3 }, online[1].Friends);
		Assert.Empty(online[2].Friends);

		// a user offline on this copy has no cache, the change is a no-op for them
		copy2.Apply(new BlockChange { UserId = 9, OtherId = 1, Blocked = true });
		Assert.False(copy2.HasBlocked(9, 1));
		Assert.Equal(new long[] { 3 }, online[1].Friends);
	}

	// Two copies on one Redis, the change published on copy 1 reaches the caches of copy 2
	[ProbeRedisFact]
	public async Task ABlockPublishedOnOneCopyReachesTheCachesOfAnother()
	{
		string connection = Environment.GetEnvironmentVariable(ProbeRedisFactAttribute.Variable)!;
		IConnectionMultiplexer redis1 = await ConnectionMultiplexer.ConnectAsync(connection);
		IConnectionMultiplexer redis2 = await ConnectionMultiplexer.ConnectAsync(connection);
		RedisChannel channel = RedisChannel.Literal("probe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ":realtime.blocks");
		try
		{
			ConcurrentDictionary<long, UserSocialContainer> online2 = new();
			online2[1] = Social(friends: new long[] { 2 });
			online2[2] = Social(friends: new long[] { 1 });
			BlockCaches copy1 = new BlockCaches(_ => null);
			BlockCaches copy2 = new BlockCaches(id => online2.GetValueOrDefault(id));

			BlockFanout fanout1 = await BlockFanout.StartAsync(redis1.GetSubscriber(), channel, copy1, null);
			await BlockFanout.StartAsync(redis2.GetSubscriber(), channel, copy2, null);

			await fanout1.PublishAsync(new BlockChange { UserId = 1, OtherId = 2, Blocked = true });
			await Until(() => copy2.HasBlocked(1, 2));
			Assert.Empty(online2[1].Friends);
			Assert.Empty(online2[2].Friends);

			await fanout1.PublishAsync(new BlockChange { UserId = 1, OtherId = 2, Blocked = false });
			await Until(() => !copy2.HasBlocked(1, 2));
		}
		finally
		{
			await redis1.CloseAsync();
			await redis2.CloseAsync();
		}
	}

	private static async Task Until(Func<bool> condition, int seconds = 5)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
		while (!condition() && DateTime.UtcNow < deadline)
		{
			await Task.Delay(20);
		}
		Assert.True(condition());
	}

	private static readonly GameBuild TestBuild = new GameBuild("genonline_zerohour", 1234567890, 987654321, (EKnownAnticheatID)(-1));

	// A custom lobby in setup hosted by the owner, with one more open slot when asked
	private static Lobby HostedBy(long owner, bool openSlot) => Lobby.FromLive(new LobbyLive
	{
		Id = 7000 + owner,
		Type = (int)ELobbyType.CustomGame,
		State = (int)ELobbyState.GAME_SETUP,
		Owner = owner,
		Name = "blocks",
		MapName = "Tournament Desert",
		MapPath = "maps/tournament desert/tournament desert",
		MapOfficial = true,
		CreatedAt = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc),
		StartingCash = 10000,
		Build = new LiveBuild { Game = TestBuild.Game, ExeCrc = TestBuild.ExeCRC, IniCrc = TestBuild.IniCRC, AnticheatId = -1 },
		MaxCameraHeight = 310,
		Joinability = (int)ELobbyJoinability.Public,
		Slots = Enumerable.Range(0, 8).Select(slot => slot == 0
			? new LobbyMemberLive { SlotIndex = 0, SlotState = (int)EPlayerType.SLOT_PLAYER, UserId = owner, DisplayName = "host", NameNotDeduped = "host", NameTag = 1001, Port = 8088, HasMap = true, Region = "Europe" }
			: new LobbyMemberLive { SlotIndex = slot, SlotState = (int)(slot == 1 && openSlot ? EPlayerType.SLOT_OPEN : EPlayerType.SLOT_CLOSED) }).ToList(),
		EverMembers = new List<long> { owner }
	});

	private static Task<LobbyMembershipStatus> Join(Lobby lobby, long joiner, IReadOnlySet<long>? blockers) =>
		Lobby.TransferMemberAsync(lobby, Array.Empty<Lobby>(), new JoinLobbyCommand(lobby.LobbyID, joiner, EUserSessionType.GameClient, 8089, true, null, TestBuild),
			new UserSession(joiner, EUserSessionType.GameClient, "", "", "", 0, 0), "guest", 1002, new UserLobbyPreferences(), blockers);

	// The host is not cached on this copy, as after a block made through another copy that never reached this one
	[Fact]
	public async Task ALobbyJoinRefusesAStoredBlockTheCacheDoesNotHoldInTheSameOrderAsBefore()
	{
		Assert.Equal(LobbyMembershipStatus.BlockedByOwner, await Join(HostedBy(9_200_001, openSlot: true), 9_200_002, new HashSet<long> { 9_200_001 }));

		// a full lobby still answers full first, as the cached check did
		Assert.Equal(LobbyMembershipStatus.NoOpenSlot, await Join(HostedBy(9_200_003, openSlot: false), 9_200_002, new HashSet<long> { 9_200_003 }));

		// a block of the joiner by someone who is not the host does not refuse the join
		Assert.Equal(LobbyMembershipStatus.NoOpenSlot, await Join(HostedBy(9_200_004, openSlot: false), 9_200_002, new HashSet<long> { 9_200_005 }));
	}

	// The matchmaking pairing check reads the cached lists, which a block message from another copy updates
	[Fact]
	public void MatchmakingDoesNotPairUsersAfterABlockMadeOnAnotherCopy()
	{
		const long queued = 9_300_001, joining = 9_300_002;
		var shared = (ConcurrentDictionary<long, SharedUserData>)typeof(WebSocketManager).GetField("m_dictSharedUserData", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
		shared[queued] = new SharedUserData(queued, Social(), "queued", 1, new PlayerStats(queued, 1500, 0, 1500));
		shared[joining] = new SharedUserData(joining, Social(), "joining", 2, new PlayerStats(joining, 1500, 0, 1500));
		try
		{
			UserSession queuedSession = new UserSession(queued, EUserSessionType.GameClient, "", "", "", 0, 0);
			UserSession joiningSession = new UserSession(joining, EUserSessionType.GameClient, "", "", "", 0, 0);
			MatchmakingManager.MatchmakingBucket bucket = new MatchmakingManager.MatchmakingBucket(0, queuedSession, 2, 2, new ConcurrentList<int>(new[] { 0 }), TestBuild);
			Assert.False(bucket.IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(joiningSession, joining));

			// the queued player blocks the joiner through another copy, the message updates this copy's cache
			RealtimeBlocks.Local.Apply(BlockFanout.Deserialize(BlockFanout.Serialize(new BlockChange { UserId = queued, OtherId = joining, Blocked = true }))!);
			Assert.True(bucket.IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(joiningSession, joining));

			RealtimeBlocks.Local.Apply(new BlockChange { UserId = queued, OtherId = joining, Blocked = false });
			Assert.False(bucket.IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(joiningSession, joining));

			// the other direction counts too
			RealtimeBlocks.Local.Apply(new BlockChange { UserId = joining, OtherId = queued, Blocked = true });
			Assert.True(bucket.IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(joiningSession, joining));
		}
		finally
		{
			shared.TryRemove(queued, out _);
			shared.TryRemove(joining, out _);
		}
	}

	private sealed class Factory : IDbContextFactory<AppDbContext>
	{
		private readonly ProbeDatabase m_probe;

		public Factory(ProbeDatabase probe)
		{
			m_probe = probe;
		}

		public AppDbContext CreateDbContext() => m_probe.Open();
	}

	private static async Task<long> AddUser(ProbeDatabase probe, string name)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.ID;
	}

	// The blocks run through SocialActions where none of the users is online, as on another copy, then the database decides here
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task JoinsMatchesAndReloadsReadTheBlocksAnotherCopyStored(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Factory factory = new Factory(probe);

		long host = await AddUser(probe, "BlockHost");
		long guest = await AddUser(probe, "BlockGuest");
		long bystander = await AddUser(probe, "BlockBystander");
		Assert.True((await SocialActions.BlockAsync(factory, host, guest)).Ok);

		// the lobby join reads who of the members blocked the joiner
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(new[] { host }, await RealtimeBlocks.BlockersAsync(db, guest, new[] { host, bystander }));
			Assert.Empty((await RealtimeBlocks.BlockersAsync(db, host, new[] { guest, bystander }))!);

			// a formed match reads every block among its players in both directions
			Assert.Equal(new[] { (host, guest) }, await RealtimeBlocks.BlocksAmongAsync(db, new[] { guest, host }));
			Assert.Empty((await RealtimeBlocks.BlocksAmongAsync(db, new[] { guest, bystander }))!);
		}
		Assert.Equal(LobbyMembershipStatus.BlockedByOwner, await Join(HostedBy(host, openSlot: true), guest, await Blockers(probe, guest, host)));

		// a cache that missed the change is replaced with the stored list by the reload a message or a contradicting decision runs
		Dictionary<long, UserSocialContainer> online = new() { [host] = Social(blocked: new[] { bystander }) };
		BlockCaches caches = new BlockCaches(id => online.GetValueOrDefault(id));
		await caches.ReloadAsync(factory, host);
		Assert.Equal(new[] { guest }, online[host].Blocked);

		Assert.True((await SocialActions.UnblockAsync(factory, host, guest)).Ok);
		await caches.ReloadAsync(factory, host);
		Assert.Empty(online[host].Blocked);
		Assert.Empty((await Blockers(probe, guest, host))!);
	}

	private static async Task<HashSet<long>?> Blockers(ProbeDatabase probe, long joiner, long host)
	{
		await using AppDbContext db = probe.Open();
		return await RealtimeBlocks.BlockersAsync(db, joiner, new[] { host });
	}
}
