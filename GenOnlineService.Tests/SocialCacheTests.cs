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
using System.Collections.Concurrent;
using System.Reflection;
using Xunit;

namespace GenOnlineService.Tests;

// Friend decisions stay current across copies: cached sets follow every copy's changes, friends-only joins and the friend limit read the database
public class SocialCacheTests
{
	static SocialCacheTests()
	{
		string root = AppContext.BaseDirectory;
		while (!File.Exists(Path.Combine(root, "GenServices.sln")))
		{
			root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found");
		}
		RoomCatalog.Initialize(Path.Combine(root, "GenOnlineService", "data", "rooms.json"));
	}

	private static UserSocialContainer Social(long[]? friends = null, long[]? pending = null, long[]? blocked = null) => new UserSocialContainer
	{
		Friends = new HashSet<long>(friends ?? Array.Empty<long>()),
		PendingRequests = new HashSet<long>(pending ?? Array.Empty<long>()),
		Blocked = new HashSet<long>(blocked ?? Array.Empty<long>())
	};

	private static ConcurrentDictionary<long, SharedUserData> Shared() =>
		(ConcurrentDictionary<long, SharedUserData>)typeof(WebSocketManager).GetField("m_dictSharedUserData", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

	private static void Online(long userID, UserSocialContainer social) =>
		Shared()[userID] = new SharedUserData(userID, social, "user" + userID, 1, new PlayerStats(userID, 1500, 0, 1500));

	[Fact]
	public void ASocialChangeNamesTheUsersWhoseSetsEveryCopyReadsAgain()
	{
		string payload = SocialFanout.Serialize(new SocialChange { UserIds = new long[] { 1, 2 } });
		Assert.Equal("{\"user_ids\":[1,2]}", payload);
		Assert.Equal(new long[] { 1, 2 }, SocialFanout.Deserialize(payload)!.UserIds);
	}

	private static readonly GameBuild TestBuild = new GameBuild("genonline_zerohour", 1234567890, 987654321, (EKnownAnticheatID)(-1));

	private static Lobby HostedBy(long owner, bool openSlot, bool friendsOnly) => Lobby.FromLive(new LobbyLive
	{
		Id = 8000 + owner % 1000,
		Type = (int)ELobbyType.CustomGame,
		State = (int)ELobbyState.GAME_SETUP,
		Owner = owner,
		Name = "friends",
		MapName = "Tournament Desert",
		MapPath = "maps/tournament desert/tournament desert",
		MapOfficial = true,
		CreatedAt = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc),
		StartingCash = 10000,
		Build = new LiveBuild { Game = TestBuild.Game, ExeCrc = TestBuild.ExeCRC, IniCrc = TestBuild.IniCRC, AnticheatId = -1 },
		MaxCameraHeight = 310,
		Joinability = (int)(friendsOnly ? ELobbyJoinability.FriendsOnly : ELobbyJoinability.Public),
		Slots = Enumerable.Range(0, 8).Select(slot => slot == 0
			? new LobbyMemberLive { SlotIndex = 0, SlotState = (int)EPlayerType.SLOT_PLAYER, UserId = owner, DisplayName = "host", NameNotDeduped = "host", NameTag = 1001, Port = 8088, HasMap = true, Region = "Europe" }
			: new LobbyMemberLive { SlotIndex = slot, SlotState = (int)(slot == 1 && openSlot ? EPlayerType.SLOT_OPEN : EPlayerType.SLOT_CLOSED) }).ToList(),
		EverMembers = new List<long> { owner }
	});

	private static Task<LobbyMembershipStatus> Join(Lobby lobby, long joiner, IReadOnlySet<long>? friends) =>
		Lobby.TransferMemberAsync(lobby, Array.Empty<Lobby>(), new JoinLobbyCommand(lobby.LobbyID, joiner, EUserSessionType.GameClient, 8089, true, null, TestBuild),
			new UserSession(joiner, EUserSessionType.GameClient, "", "", "", 0, 0), "guest", 1002, new UserLobbyPreferences(), new HashSet<long>(), friends);

	// The host's cache still lists the joiner, as after an unfriend made through another copy that never reached this one
	[Fact]
	public async Task AFriendsOnlyJoinFollowsTheStoredFriendshipOverAStaleCache()
	{
		const long host = 9_500_001, joiner = 9_500_002;
		Online(host, Social(friends: new[] { joiner }));
		try
		{
			Assert.Equal(LobbyMembershipStatus.FriendshipRequired, await Join(HostedBy(host, openSlot: true, friendsOnly: true), joiner, new HashSet<long>()));

			// a full lobby still answers full first
			Assert.Equal(LobbyMembershipStatus.NoOpenSlot, await Join(HostedBy(host, openSlot: false, friendsOnly: true), joiner, new HashSet<long>()));
		}
		finally
		{
			Shared().TryRemove(host, out _);
		}

		// without a session of the host here the stored friendship still decides
		Assert.Equal(LobbyMembershipStatus.FriendshipRequired, await Join(HostedBy(9_500_003, openSlot: true, friendsOnly: true), joiner, new HashSet<long>()));
	}

	// The host creating a friends-only lobby is not its own friend and still takes its seat
	[Fact]
	public async Task TheHostCreatingAFriendsOnlyLobbyJoinsIt()
	{
		const long host = 9_550_001;
		LobbyLive live = new LobbyLive
		{
			Id = 8551,
			Type = (int)ELobbyType.CustomGame,
			State = (int)ELobbyState.GAME_SETUP,
			Owner = host,
			Name = "friends",
			MapName = "Tournament Desert",
			MapPath = "maps/tournament desert/tournament desert",
			MapOfficial = true,
			CreatedAt = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc),
			StartingCash = 10000,
			Build = new LiveBuild { Game = TestBuild.Game, ExeCrc = TestBuild.ExeCRC, IniCrc = TestBuild.IniCRC, AnticheatId = -1 },
			MaxCameraHeight = 310,
			Joinability = (int)ELobbyJoinability.FriendsOnly,
			Slots = Enumerable.Range(0, 8).Select(slot => new LobbyMemberLive { SlotIndex = slot, SlotState = (int)(slot < 2 ? EPlayerType.SLOT_OPEN : EPlayerType.SLOT_CLOSED) }).ToList(),
			EverMembers = new List<long>()
		};
		Online(host, Social());
		try
		{
			Lobby lobby = Lobby.FromLive(live);
			Assert.Equal(LobbyMembershipStatus.Success, await lobby.AddCreatorAsync(new UserSession(host, EUserSessionType.GameClient, "", "", "", 0, 0), "host", 1001, 8088, new UserLobbyPreferences()));
			Assert.NotNull(lobby.GetMemberFromUserID(host));
		}
		finally
		{
			Shared().TryRemove(host, out _);
		}
	}

	private static UserSession Viewer(long userID) => new UserSession(userID, EUserSessionType.ChatClient, "", "", "", 0, 0);

	// A restored lobby whose host has no session here stays out of the list until the host's sets are read, so a blocked viewer never sees it
	[Fact]
	public void TheLobbyListHidesTheLobbyOfAHostWithoutSetsHere()
	{
		Lobby lobby = HostedBy(9_600_001, openSlot: true, friendsOnly: false);
		Assert.Null(LobbyHosts.Of(9_600_001));
		Assert.False(RealtimeLobbies.IsListedFor(lobby, Viewer(9_600_002)));
		Assert.True(RealtimeLobbies.IsListedFor(lobby, Viewer(9_600_001)));
	}

	// The lobby list reads the host's cached sets, which the reload of a social change keeps current
	[Fact]
	public void TheLobbyListFollowsTheHostsBlocksAndFriends()
	{
		const long host = 9_700_001, blocked = 9_700_002, friend = 9_700_003, other = 9_700_004;
		Online(host, Social(friends: new[] { friend }, blocked: new[] { blocked }));
		try
		{
			Lobby open = HostedBy(host, openSlot: true, friendsOnly: false);
			Lobby friendsOnly = HostedBy(host, openSlot: true, friendsOnly: true);
			Assert.False(RealtimeLobbies.IsListedFor(open, Viewer(blocked)));
			Assert.True(RealtimeLobbies.IsListedFor(open, Viewer(other)));
			Assert.True(RealtimeLobbies.IsListedFor(friendsOnly, Viewer(friend)));
			Assert.False(RealtimeLobbies.IsListedFor(friendsOnly, Viewer(other)));

			// an unfriend another copy made, as its message applies here
			SocialState.Update(host, c => c.Friends.Remove(friend));
			Assert.False(RealtimeLobbies.IsListedFor(friendsOnly, Viewer(friend)));
		}
		finally
		{
			Shared().TryRemove(host, out _);
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

	// The changes run through SocialActions where none of the users is online, as on another copy, then this copy's reload and decisions read them
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task FriendChangesAnotherCopyStoredReachTheCachesAndTheDecisionsHere(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Factory factory = new Factory(probe);

		long host = await AddUser(probe, "FriendHost");
		long guest = await AddUser(probe, "FriendGuest");
		long bystander = await AddUser(probe, "FriendBystander");
		Dictionary<long, UserSocialContainer> copy2 = new() { [host] = Social(), [guest] = Social() };
		BlockCaches caches = new BlockCaches(id => copy2.GetValueOrDefault(id));

		// a request made elsewhere shows as incoming once the change is read here
		Assert.Equal((string?)null, (await SocialActions.SendFriendRequestAsync(factory, guest, host)).Code);
		await caches.ReloadAsync(factory, host);
		await caches.ReloadAsync(factory, guest);
		Assert.Equal(new[] { guest }, copy2[host].PendingRequests);

		Assert.True((await SocialActions.AcceptFriendRequestAsync(factory, host, guest)).Ok);
		await caches.ReloadAsync(factory, host);
		await caches.ReloadAsync(factory, guest);
		Assert.Equal(new[] { guest }, copy2[host].Friends);
		Assert.Equal(new[] { host }, copy2[guest].Friends);
		Assert.Empty(copy2[host].PendingRequests);

		// a late block message removes the friendship, the reload of both users brings back the stored one
		caches.Apply(new BlockChange { UserId = host, OtherId = guest, Blocked = true });
		Assert.Empty(copy2[guest].Friends);
		await caches.ReloadAsync(factory, host);
		await caches.ReloadAsync(factory, guest);
		Assert.Equal(new[] { guest }, copy2[host].Friends);
		Assert.Equal(new[] { host }, copy2[guest].Friends);
		Assert.Empty(copy2[host].Blocked);

		// a friends-only join reads the friendship among the members
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(new[] { host }, (await RealtimeBlocks.FriendsAmongAsync(db, guest, new[] { host, bystander }))!);
			Assert.Empty((await RealtimeBlocks.FriendsAmongAsync(db, bystander, new[] { host, guest }))!);
		}

		// a lobby host without a session here gets its sets read for the list
		LobbyHosts.Forget(host);
		await LobbyHosts.LoadAsync(factory, host);
		try
		{
			Lobby friendsOnly = HostedBy(host, openSlot: true, friendsOnly: true);
			Assert.True(RealtimeLobbies.IsListedFor(friendsOnly, Viewer(guest)));
			Assert.False(RealtimeLobbies.IsListedFor(friendsOnly, Viewer(bystander)));
			Assert.True((await SocialActions.BlockAsync(factory, host, bystander)).Ok);
			await RealtimeBlocks.Local.ReloadAsync(factory, host);
			Assert.False(RealtimeLobbies.IsListedFor(HostedBy(host, openSlot: true, friendsOnly: false), Viewer(bystander)));
		}
		finally
		{
			LobbyHosts.Forget(host);
		}

		// an unfriend elsewhere leaves the caches once read
		Assert.True((await SocialActions.RemoveFriendAsync(factory, guest, host)).Ok);
		await caches.ReloadAsync(factory, host);
		await caches.ReloadAsync(factory, guest);
		Assert.Empty(copy2[host].Friends);
		Assert.Empty(copy2[guest].Friends);
	}

	// The friend limit counts stored friendships, a cache that missed friendships made elsewhere does not let a user past it
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheFriendLimitCountsStoredFriendshipsOverAStaleCache(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Factory factory = new Factory(probe);

		long me = await AddUser(probe, "FullFriends");
		long target = await AddUser(probe, "FullTarget");
		await using (AppDbContext db = probe.Open())
		{
			for (long i = 0; i < SocialActions.FriendsLimit; i++)
			{
				long other = 9_900_000_000 + i;
				db.Friendships.Add(new Friendship { UserIdLow = Math.Min(me, other), UserIdHigh = Math.Max(me, other), CreatedAt = DateTime.UtcNow });
			}
			await db.SaveChangesAsync();
		}

		Online(me, Social());
		try
		{
			Result<Empty> result = await SocialActions.SendFriendRequestAsync(factory, me, target);
			Assert.False(result.Ok);
			Assert.Equal(ErrorCodes.SocialFriendListFull, result.Code);
		}
		finally
		{
			Shared().TryRemove(me, out _);
		}
	}
}
