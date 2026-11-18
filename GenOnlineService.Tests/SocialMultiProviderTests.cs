using Database;
using GenOnlineService.Names;
using Xunit;

namespace GenOnlineService.Tests;

// The social and user reads are shared by every database provider of one process. A query compiled once is bound to the first model it
// ran against and fails on a second one, which GetFriends swallowed into an empty friend list
public class SocialMultiProviderTests
{
	private static async Task<long> AddUser(ProbeDatabase probe, string name)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.ID;
	}

	[Fact]
	public async Task TheSocialReadsWorkOnEveryProviderOfOneProcess()
	{
		foreach (string provider in new[] { "postgres", "mariadb", "mysql" })
		{
			await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
			if (probe == null) continue;

			long me = await AddUser(probe, "Reader");
			long friend = await AddUser(probe, "Friend");
			long blocked = await AddUser(probe, "Blocked");
			long requester = await AddUser(probe, "Requester");

			await using AppDbContext db = probe.Open();
			await Database.Social.CreateFriendship(db, me, friend);
			await Database.Social.AddBlock(db, me, blocked);
			db.FriendRequests.Add(new FriendRequest { FromUserId = requester, ToUserId = me, CreatedAt = DateTime.UtcNow });
			await db.SaveChangesAsync();

			Assert.Equal(new HashSet<long> { friend }, await Database.Social.GetFriends(db, me));
			Assert.Equal(new HashSet<long> { blocked }, await Database.Social.GetBlocked(db, me));
			Assert.Equal(new HashSet<long> { requester }, await Database.Social.GetPendingFriendsRequests(db, me));
			Assert.Equal("Reader", await Database.Users.GetDisplayName(db, me));
			Assert.NotNull(await Database.Users.GetUserLobbyPreferences(db, me));
		}
	}
}
