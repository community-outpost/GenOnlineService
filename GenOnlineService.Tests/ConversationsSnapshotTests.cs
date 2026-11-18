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
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GenOnlineService.Tests;

// The direct conversations of the Conversations snapshot. Reloads the process-wide StaffRoles cache, so it runs alone
[Collection(AdminAreaCollection.Name)]
public class ConversationsSnapshotTests
{
	private static async Task<long> AddUser(ProbeDatabase probe, string name)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.ID;
	}

	// Only friends exchange direct messages, so a former friend's unread message is not listed: the game client could not open it
	// and would notify about it on every sign-in. A staff conversation stays listed with or without a friendship or block.
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheSnapshotListsFriendsAndStaffButNotFormerFriends(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		long me = await AddUser(probe, "Reader");
		long friend = await AddUser(probe, "Friend");
		long former = await AddUser(probe, "Former");
		long blockedFriend = await AddUser(probe, "Blocked");
		long moderator = await AddUser(probe, "Moderator");

		await using (AppDbContext db = probe.Open())
		{
			await Database.Social.CreateFriendship(db, me, friend);
			await Database.Social.CreateFriendship(db, me, former);
			await Database.Social.CreateFriendship(db, me, blockedFriend);
			foreach (long sender in new[] { friend, former, blockedFriend, moderator })
			{
				await Database.Chat.InsertDirectMessage(db, sender, me, "chat", "hello");
			}
			await Database.Social.RemoveFriendship(db, me, former);
			await Database.Social.AddBlock(db, me, blockedFriend);
			await Database.Social.AddBlock(db, me, moderator);
			db.UserRoles.Add(new UserRole { UserID = moderator, Role = StaffRoles.Moderator, Source = AuthoritySources.Local, GrantedAt = DateTime.UtcNow, GrantedBy = "test" });
			await db.SaveChangesAsync();
			await StaffRoles.Reload(db);
		}

		try
		{
			await using AppDbContext db = probe.Open();
			HashSet<long> blocked = await RealtimeHub.BlockedEitherWayAsync(db, me);
			List<Database.DirectConversation> listed = await RealtimeHub.ListedDirectConversationsAsync(db, me, blocked);

			Assert.Equal(new[] { friend, moderator }.Order(), listed.Select(c => c.OtherUserID).Order());
			Assert.Equal(1, listed.Single(c => c.OtherUserID == friend).Unread);
			Assert.Equal(1, listed.Single(c => c.OtherUserID == moderator).Unread);
			// the former friend's message stays stored, it is only not listed
			Assert.True(await Database.Chat.HasDirectMessage(db, former, me));
		}
		finally
		{
			await using AppDbContext db = probe.Open();
			await db.UserRoles.Where(r => r.UserID == moderator).ExecuteDeleteAsync();
			await StaffRoles.Reload(db);
		}
	}
}
