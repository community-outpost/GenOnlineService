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

using GenOnlineService.Identity;
using GenOnlineService.Realtime;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

using static FriendsPagesTests;

// The Blocked players page of the account area (T19b) and the blocks the Friends page makes: the same SocialActions as the
// game, so a block made on the web ends friendships, clears requests and reaches the caches and the game the same way
[Collection(AdminAreaCollection.Name)]
public class BlockedPagesTests
{
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheBlockedPageNeedsASignedInPlayer(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());

		PageResponse page = await browser.Get(Blocked);
		Assert.Equal(302, page.Status);
		Assert.Equal("http://accounts.test/users/sign_in?ReturnUrl=%2Fprofile%2Fblocked", page.Location);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheSidebarListsBlockedPlayersAndMarksTheCurrentPage(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await SignedUp(probe, "Sidebar", "sidebarblocked@example.org");

		PageResponse profile = await browser.Get("/profile");
		Assert.Matches(@"<a href=""/profile/blocked""><svg[^>]*>.*?</svg>Blocked players</a>", profile.Body);
		PageResponse friends = await browser.Get(Friends);
		Assert.DoesNotContain("<a href=\"/profile/blocked\" aria-current", friends.Body);

		PageResponse blocked = await browser.Get(Blocked);
		Assert.Matches(@"<a href=""/profile/blocked"" aria-current=""page"">.*?Blocked players</a>", blocked.Body);
		Assert.DoesNotContain("<a href=\"/profile/friends\" aria-current", blocked.Body);
		Assert.DoesNotContain("<a href=\"/profile\" aria-current", blocked.Body);
		Assert.Contains("<p class=\"gp-crumbs\">Your account / Blocked players</p><h1 class=\"gp-title\">Blocked players</h1>", blocked.Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheBlockedPageListsTheBlockedPlayers(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Blocker", "blocker@example.org");
		long toxic = await AddUser(probe, "ToxicBob", 6612);
		await Block(probe, me.ID, toxic);

		PageResponse page = await browser.Get(Blocked);
		Assert.Equal(200, page.Status);
		Assert.Contains("ToxicBob<span class=\"gp-tag\">#6612</span>", page.Body);
		Assert.Contains("Blocked · 2026-11-02", page.Body);
		Assert.Matches($@"<form method=""post"" action=""/profile/blocked/remove""[^>]*>.*?name=""user_id"" value=""{toxic}"".*?Unblock</button></form>", page.Body);
		Assert.Contains("action=\"/profile/blocked/add\"", page.Body);
		Assert.Contains("Block a player", page.Body);
		Assert.Contains("Players you blocked here or in the game.", page.Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task NoBlockedPlayersUsesTheEmptyState(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await SignedUp(probe, "Easygoing", "easygoing@example.org");

		PageResponse page = await browser.Get(Blocked);
		Assert.Contains("class=\"gp-empty\"", page.Body);
		Assert.Contains("No blocked players", page.Body);
		Assert.DoesNotContain("/profile/blocked/remove", page.Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AHostileNameIsEncodedAndTheClassesAreDefined(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Reader", "readerblocked@example.org");
		long blocked = await AddUser(probe, "\"><svg onload=alert(3)>", 3);
		await Block(probe, me.ID, blocked);

		PageResponse page = await browser.Get(Blocked);
		Assert.Equal(200, page.Status);
		Assert.DoesNotContain("<svg onload", page.Body);
		Assert.Contains("&quot;&gt;&lt;svg onload=alert(3)&gt;", page.Body);
		Assert.DoesNotMatch("(src|href|action)=\"(https?:)?//", page.Body);
		Assert.DoesNotContain("<script", page.Body);
		Assert.Equal("DENY", page.FrameOptions);

		string generals = File.ReadAllText(Path.Combine(RepositoryRoot(), "GenOnlineService", "wwwroot", "portal", "generals.css"));
		(AccountsBrowser empty, _) = await SignedUp(probe, "Bare", "bareblocked@example.org");
		foreach (PageResponse each in new[] { page, await empty.Get(Blocked) })
		{
			string[] missing = ClassesIn(each.Body).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => c.StartsWith("gp", StringComparison.Ordinal)).Distinct()
				.Where(c => !Regex.IsMatch(generals, Regex.Escape("." + c) + @"[ :{,>\[.]")).ToArray();
			Assert.True(missing.Length == 0, "not in generals.css: " + String.Join(", ", missing));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task OnlyKnownStatusCodesShowAMessageHere(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await SignedUp(probe, "Reflect", "reflectblocked@example.org");

		Assert.DoesNotContain("class=\"gp-alert", (await browser.Get("/profile/blocked?r=%3Cb%3Ehello%3C%2Fb%3E")).Body);
		// a status that belongs to the other page is not shown here, and the other way round
		Assert.DoesNotContain("class=\"gp-alert", (await browser.Get("/profile/blocked?r=accepted")).Body);
		Assert.DoesNotContain("class=\"gp-alert", (await browser.Get("/profile/friends?r=unblocked")).Body);
		Assert.Contains("class=\"gp-alert gp-alert-success\"", (await browser.Get("/profile/blocked?r=blocked")).Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task BlockingByNameOrIdAndUnblocking(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Shield", "shield@example.org");
		long byName = await AddUser(probe, "Nuisance", 13);
		long byId = await AddUser(probe, "Pest", 14);

		PageResponse answer = await Post(browser, Blocked, "/profile/blocked/add", ("player", "nuisance#0013"));
		Assert.Equal(302, answer.Status);
		Assert.Equal("/profile/blocked?r=blocked", answer.Location);
		Assert.True(await HasBlock(probe, me.ID, byName));
		Assert.Contains("Player blocked.", (await browser.Get(answer.Location!)).Body);

		Assert.Equal("/profile/blocked?r=blocked", (await Post(browser, Blocked, "/profile/blocked/add", ("player", byId.ToString()))).Location);
		Assert.True(await HasBlock(probe, me.ID, byId));
		// blocking again is no error and no second row
		Assert.Equal("/profile/blocked?r=blocked", (await Post(browser, Blocked, "/profile/blocked/add", ("player", byId.ToString()))).Location);
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(2, await db.Blocks.CountAsync(b => b.UserId == me.ID));
		}

		answer = await Post(browser, Blocked, "/profile/blocked/remove", ("user_id", byName.ToString()));
		Assert.Equal("/profile/blocked?r=unblocked", answer.Location);
		Assert.False(await HasBlock(probe, me.ID, byName));
		Assert.True(await HasBlock(probe, me.ID, byId));
		Assert.Contains("Player unblocked.", (await browser.Get(answer.Location!)).Body);
	}

	// a name needs its tag here too, and every refusal is the same neutral message with nothing stored
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EveryRefusedBlockSaysTheSameThing(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Careful", "careful@example.org");
		await AddUser(probe, "Target", 42);
		await AddUser(probe, "Target", 43);

		foreach (string attempt in new[] { "Target", "Targe#0042", "Target#0044", "Target#", "999999", "", "Nobody#0001" })
		{
			PageResponse answer = await Post(browser, Blocked, "/profile/blocked/add", ("player", attempt));
			Assert.Equal("/profile/blocked?r=neutral", answer.Location);
		}
		Assert.Contains(Neutral, Decoded(await browser.Get("/profile/blocked?r=neutral")));
		Assert.Equal("/profile/blocked?r=self", (await Post(browser, Blocked, "/profile/blocked/add", ("player", Handle(me)))).Location);
		await using AppDbContext db = probe.Open();
		Assert.False(await db.Blocks.AnyAsync());
	}

	// blocking ends a friendship and the requests between the two, as in the game
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task BlockingAFriendEndsTheFriendshipAndTheRequests(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Parting", "parting@example.org");
		long friend = await AddUser(probe, "Friend", 1);
		long asker = await AddUser(probe, "Asker", 2);
		await Befriend(probe, me.ID, friend);
		await Request(probe, asker, me.ID);

		// from a friend's menu on the Friends page
		PageResponse answer = await Post(browser, Friends, "/profile/friends/block", ("user_id", friend.ToString()));
		Assert.Equal("/profile/friends?r=blocked", answer.Location);
		Assert.True(await HasBlock(probe, me.ID, friend));
		Assert.False(await AreFriends(probe, me.ID, friend));
		Assert.Equal("/profile/friends?r=gone", (await Post(browser, Friends, "/profile/friends/block", ("user_id", "x"))).Location);

		await Post(browser, Blocked, "/profile/blocked/add", ("player", asker.ToString()));
		Assert.False(await HasRequest(probe, asker, me.ID));
		Assert.True(await HasBlock(probe, me.ID, asker));
	}

	// a holder of social.unblockable (an admin) cannot be blocked, and the answer is the one every refusal gives
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAdminCannotBeBlocked(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await SignedUp(probe, "Annoyed", "annoyed@example.org");
		long admin = await AddUser(probe, "Chief", 1);
		long plain = await AddUser(probe, "Plain", 2);
		await using (AppDbContext db = probe.Open())
		{
			db.UserRoles.Add(new UserRole { UserID = admin, Role = StaffRoles.Admin, Source = AuthoritySources.Local, GrantedAt = DateTime.UtcNow, GrantedBy = "test" });
			await db.SaveChangesAsync();
			await StaffRoles.Reload(db);
		}

		Assert.Equal("/profile/blocked?r=neutral", (await Post(browser, Blocked, "/profile/blocked/add", ("player", "Chief#0001"))).Location);
		Assert.Equal("/profile/blocked?r=neutral", (await Post(browser, Blocked, "/profile/blocked/add", ("player", admin.ToString()))).Location);
		Assert.Equal("/profile/blocked?r=neutral", (await Post(browser, Blocked, "/profile/blocked/add", ("player", "Nobody#0099"))).Location);
		await using (AppDbContext db = probe.Open())
		{
			Assert.False(await db.Blocks.AnyAsync(b => b.BlockedUserId == admin));
		}
		Assert.Contains(Neutral, Decoded(await browser.Get("/profile/blocked?r=neutral")));
		Assert.Equal("/profile/blocked?r=blocked", (await Post(browser, Blocked, "/profile/blocked/add", ("player", plain.ToString()))).Location);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EveryBlockPostNeedsTheFormToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Guarded", "guardedblocks@example.org");
		long other = await AddUser(probe, "Other", 1);
		await Block(probe, me.ID, other);
		long third = await AddUser(probe, "Third", 2);

		foreach ((string action, string field, string value) in new[]
		{
			("/profile/friends/block", "user_id", third.ToString()),
			("/profile/blocked/add", "player", "Third#0002"),
			("/profile/blocked/remove", "user_id", other.ToString())
		})
		{
			PageResponse answer = await browser.Post(action, new() { [field] = value });
			Assert.Equal(400, answer.Status);
			Assert.Null(answer.Location);
		}
		Assert.True(await HasBlock(probe, me.ID, other));
		Assert.False(await HasBlock(probe, me.ID, third));
	}

	// adding a friend and blocking draw on one limit of 20 in 15 minutes for each account
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task BlockingSharesTheAddLimit(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Prober", "proberblocks@example.org");
		long target = await AddUser(probe, "Target", 1);

		string token = AccountsBrowser.Token(await browser.Get(Blocked));
		for (int i = 0; i < 20; i++)
		{
			string action = i % 2 == 0 ? "/profile/friends/add" : "/profile/blocked/add";
			Assert.Equal(302, (await browser.Post(action, new() { ["__go_consent_token"] = token, ["player"] = "Nobody#000" + (i % 10) })).Status);
		}
		PageResponse limited = await browser.Post("/profile/blocked/add", new() { ["__go_consent_token"] = token, ["player"] = target.ToString() });
		Assert.Equal(429, limited.Status);
		Assert.Contains("Too many tries.", limited.Body);
		Assert.Contains("class=\"gp-side\"", limited.Body);
		Assert.False(await HasBlock(probe, me.ID, target));
		Assert.Equal(429, (await browser.Post("/profile/friends/add", new() { ["__go_consent_token"] = token, ["player"] = target.ToString() })).Status);

		// the lists' own buttons are not limited: they act on players the page showed
		Assert.Equal("/profile/blocked?r=unblocked", (await browser.Post("/profile/blocked/remove", new() { ["__go_consent_token"] = token, ["user_id"] = target.ToString() })).Location);
	}

	// the game hears of a block made on the web as it does of one made in the game
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ABlockOnTheWebReachesTheGameLikeOneFromTheGame(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser webA, User a) = await SignedUp(probe, "WebOne", "webone@example.org");
		(_, User b) = await SignedUp(probe, "WebTwo", "webtwo@example.org");
		long c = await AddUser(probe, "GameOne", 31);
		long d = await AddUser(probe, "GameTwo", 32);
		long[] everyone = [a.ID, b.ID, c, d];
		foreach (long id in everyone)
		{
			Online(id, "online" + id, 1);
		}
		try
		{
			using RealtimeLog log = RealtimeLog.Begin();
			var factory = new ProbeFactory(probe);
			await SocialActions.SendFriendRequestAsync(factory, a.ID, b.ID);
			await SocialActions.AcceptFriendRequestAsync(factory, b.ID, a.ID);
			await SocialActions.SendFriendRequestAsync(factory, c, d);
			await SocialActions.AcceptFriendRequestAsync(factory, d, c);
			Assert.Equal(new[] { b.ID }, Friends_(a.ID));
			log.Clear();

			// blocking a friend: the friendship ends for both, and the blocker's cache lists the block
			await Post(webA, Blocked, "/profile/blocked/add", ("player", b.ID.ToString()));
			Assert.True((await SocialActions.BlockAsync(factory, c, d)).Ok);
			Assert.Contains("FriendRemoved", log.Events(a.ID));
			Assert.Contains("FriendRemoved", log.Events(b.ID));
			Assert.Equal(new[] { b.ID }, Blocked_(a.ID));
			Assert.Empty(Friends_(a.ID));
			Assert.Empty(Friends_(b.ID));
			Assert.Equal(Normalised(log.Events(a.ID), log.Events(b.ID)), Normalised(log.Events(c), log.Events(d)));
			Assert.Equal(Blocked_(a.ID).Length, Blocked_(c).Length);
			log.Clear();

			// unblocking: the cache drops the block
			await Post(webA, Blocked, "/profile/blocked/remove", ("user_id", b.ID.ToString()));
			Assert.True((await SocialActions.UnblockAsync(factory, c, d)).Ok);
			Assert.Empty(Blocked_(a.ID));
			Assert.Empty(Blocked_(c));
			Assert.Equal(Normalised(log.Events(a.ID), log.Events(b.ID)), Normalised(log.Events(c), log.Events(d)));

			// a block from a friend's menu on the Friends page reaches the game the same way
			await SocialActions.SendFriendRequestAsync(factory, a.ID, b.ID);
			await SocialActions.AcceptFriendRequestAsync(factory, b.ID, a.ID);
			log.Clear();
			await Post(webA, Friends, "/profile/friends/block", ("user_id", b.ID.ToString()));
			Assert.Contains("FriendRemoved", log.Events(b.ID));
			Assert.Equal(new[] { b.ID }, Blocked_(a.ID));
		}
		finally
		{
			foreach (long id in everyone)
			{
				Shared().TryRemove(id, out _);
			}
		}
	}
}
