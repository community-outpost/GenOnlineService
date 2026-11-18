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
using GenOnlineService.Names;
using Xunit;

namespace GenOnlineService.Tests;

// Runs only when both GENONLINE_PROBE_POSTGRES and GENONLINE_PROBE_MARIADB are set
public sealed class BothProbesFactAttribute : FactAttribute
{
	public BothProbesFactAttribute()
	{
		if (Environment.GetEnvironmentVariable(ProbeDatabase.PostgresVariable) == null || Environment.GetEnvironmentVariable(ProbeDatabase.MariaDbVariable) == null)
		{
			Skip = $"Set both {ProbeDatabase.PostgresVariable} and {ProbeDatabase.MariaDbVariable} to run the cross-provider probe";
		}
	}
}

// The test process runs every probe on both providers, so a read kept as a static EF compiled query is bound to the first
// provider's model and throws on the other; these reads swallow the error and return empty, which hid former friends' tests
public class ProviderSharedQueriesTests
{
	private static async Task<long> AddUser(AppDbContext db, string name)
	{
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.ID;
	}

	private static async Task AssertSocialAndUserReads(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		long me = await AddUser(db, "Reader");
		long friend = await AddUser(db, "Friend");
		long blocked = await AddUser(db, "Blocked");
		long requester = await AddUser(db, "Requester");

		await Database.Social.CreateFriendship(db, me, friend);
		await Database.Social.AddBlock(db, me, blocked);
		await Database.Social.AddPendingFriendRequest(db, requester, me);
		db.PlayerPreferences.Add(new PlayerPreference { UserID = me, Color = 3, Map = "Tournament Desert", UpdatedAt = DateTime.UtcNow });
		await db.SaveChangesAsync();

		Assert.Equal(new[] { friend }, await Database.Social.GetFriends(db, me));
		Assert.Equal(new[] { blocked }, await Database.Social.GetBlocked(db, me));
		Assert.Equal(new[] { requester }, await Database.Social.GetPendingFriendsRequests(db, me));
		Assert.Equal("Reader", await Database.Users.GetDisplayName(db, me));
		UserLobbyPreferences? preferences = await Database.Users.GetUserLobbyPreferences(db, me);
		Assert.NotNull(preferences);
		Assert.Equal(3, preferences.favorite_color);
		Assert.Equal("Tournament Desert", preferences.favorite_map);
	}

	[BothProbesFact]
	public async Task SocialAndUserReadsWorkOnBothProvidersInOneProcess()
	{
		await using ProbeDatabase? postgres = await ProbeDatabase.Create("postgres");
		await using ProbeDatabase? mariadb = await ProbeDatabase.Create("mariadb");
		Assert.NotNull(postgres);
		Assert.NotNull(mariadb);

		await AssertSocialAndUserReads(postgres);
		await AssertSocialAndUserReads(mariadb);
	}
}
