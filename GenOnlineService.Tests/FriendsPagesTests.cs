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
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// The Friends and Blocked players pages of the account area (T19b): server-rendered, forms that post and redirect, and the
// same SocialActions the game and /api/v1/me use, so a change made on the web reaches the game live. Uses the process-wide
// staff and realtime state, so it runs with the other probes that do.
[Collection(AdminAreaCollection.Name)]
public class FriendsPagesTests
{
	internal const string Friends = "/profile/friends";
	internal const string Blocked = "/profile/blocked";
	internal const string Neutral = "We couldn't send that request.";

	internal static async Task<(AccountsBrowser Browser, User User)> SignedUp(ProbeDatabase probe, string name, string email)
	{
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, name, email, "a long enough password")).Status);
		return (browser, (await PasswordPagesTests.UserByEmail(probe, email))!);
	}

	internal static async Task<long> AddUser(ProbeDatabase probe, string name, int tag)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user.ID;
	}

	internal static string Handle(User user) => Database.DisplayNames.Format(user.Username, user.NameTag);

	// what a browser does: open the page for its form token, post the fields, and report the answer
	internal static async Task<PageResponse> Post(AccountsBrowser browser, string page, string action, params (string Name, string Value)[] fields)
	{
		PageResponse form = await browser.Get(page);
		Assert.Equal(200, form.Status);
		Dictionary<string, string> body = new() { ["__go_consent_token"] = AccountsBrowser.Token(form) };
		foreach ((string name, string value) in fields)
		{
			body[name] = value;
		}
		return await browser.Post(action, body);
	}

	internal static string Decoded(PageResponse page) => WebUtility.HtmlDecode(page.Body);

	internal static async Task<bool> HasRequest(ProbeDatabase probe, long from, long to)
	{
		await using AppDbContext db = probe.Open();
		return await db.FriendRequests.AnyAsync(r => r.FromUserId == from && r.ToUserId == to);
	}

	internal static async Task<bool> AreFriends(ProbeDatabase probe, long a, long b)
	{
		await using AppDbContext db = probe.Open();
		return await db.Friendships.AnyAsync(f => f.UserIdLow == Math.Min(a, b) && f.UserIdHigh == Math.Max(a, b));
	}

	internal static async Task<bool> HasBlock(ProbeDatabase probe, long by, long other)
	{
		await using AppDbContext db = probe.Open();
		return await db.Blocks.AnyAsync(x => x.UserId == by && x.BlockedUserId == other);
	}

	internal static async Task Befriend(ProbeDatabase probe, long a, long b)
	{
		await using AppDbContext db = probe.Open();
		db.Friendships.Add(new Friendship { UserIdLow = Math.Min(a, b), UserIdHigh = Math.Max(a, b), CreatedAt = DateTime.UtcNow });
		await db.SaveChangesAsync();
	}

	internal static async Task Request(ProbeDatabase probe, long from, long to)
	{
		await using AppDbContext db = probe.Open();
		db.FriendRequests.Add(new FriendRequest { FromUserId = from, ToUserId = to, CreatedAt = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc) });
		await db.SaveChangesAsync();
	}

	internal static async Task Block(ProbeDatabase probe, long by, long other)
	{
		await using AppDbContext db = probe.Open();
		db.Blocks.Add(new UserBlock { UserId = by, BlockedUserId = other, CreatedAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc) });
		await db.SaveChangesAsync();
	}

	internal static string RepositoryRoot()
	{
		string root = AppContext.BaseDirectory;
		while (!File.Exists(Path.Combine(root, "GenServices.sln")))
		{
			root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found");
		}
		return root;
	}

	internal static string ClassesIn(string html) => String.Join(' ', Regex.Matches(html, "class=\"([^\"]*)\"").Select(m => m.Groups[1].Value));

	// ===== the pages need a signed-in player =====

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheFriendsPageNeedsASignedInPlayer(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());

		PageResponse friends = await browser.Get(Friends);
		Assert.Equal(302, friends.Status);
		Assert.Equal("http://accounts.test/users/sign_in?ReturnUrl=%2Fprofile%2Ffriends", friends.Location);
	}

	// ===== the sidebar =====

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheSidebarListsFriendsAndMarksTheCurrentPage(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Sidebar", "sidebar@example.org");
		long asker = await AddUser(probe, "Asker", 11);
		await Request(probe, asker, me.ID);

		PageResponse profile = await browser.Get("/profile");
		Assert.Matches(@"<a href=""/profile"" aria-current=""page"">.*?Profile</a>", profile.Body);
		Assert.Matches(@"<a href=""/profile/friends""><svg[^>]*>.*?</svg>Friends<span class=""gp-count"" aria-label=""1 friend requests"">1</span></a>", profile.Body);

		PageResponse friends = await browser.Get(Friends);
		Assert.Matches(@"<a href=""/profile/friends"" aria-current=""page"">.*?Friends<span class=""gp-count""", friends.Body);
		Assert.DoesNotContain("<a href=\"/profile\" aria-current", friends.Body);
		Assert.Contains("<p class=\"gp-crumbs\">Your account / Friends</p><h1 class=\"gp-title\">Friends</h1>", friends.Body);
	}

	// ===== listing =====

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheFriendsPageListsFriendsRequestsAndSentRequests(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Lister", "lister@example.org");
		long friend = await AddUser(probe, "DesertFox", 1187);
		long other = await AddUser(probe, "Ivan_TT", 2208);
		long incoming = await AddUser(probe, "Lin_Mei", 915);
		long outgoing = await AddUser(probe, "ScudStorm", 4421);
		await Befriend(probe, me.ID, friend);
		await Befriend(probe, me.ID, other);
		await Request(probe, incoming, me.ID);
		await Request(probe, me.ID, outgoing);

		PageResponse page = await browser.Get(Friends);
		Assert.Equal(200, page.Status);
		Assert.Contains("DesertFox<span class=\"gp-tag\">#1187</span>", page.Body);
		Assert.Contains("Ivan_TT<span class=\"gp-tag\">#2208</span>", page.Body);
		Assert.Contains("Lin_Mei<span class=\"gp-tag\">#0915</span>", page.Body);
		Assert.Contains("ScudStorm<span class=\"gp-tag\">#4421</span>", page.Body);
		Assert.Contains("Your friends", page.Body);
		Assert.Contains("2 of 200 friends.", page.Body);
		Assert.Contains("Incoming: 1. Sent by you: 1.", page.Body);
		Assert.Contains("Wants to be friends · 2026-11-16", page.Body);
		Assert.Contains("You sent a request · 2026-11-16", page.Body);
		// nobody is online here, so the friends read Offline
		Assert.Contains("Offline", page.Body);

		// every control is a form that posts to its action with the player's id and the antiforgery token
		Assert.Matches($@"<form method=""post"" action=""/profile/friends/accept""[^>]*>.*?name=""user_id"" value=""{incoming}"".*?Accept</button></form>", page.Body);
		Assert.Matches($@"<form method=""post"" action=""/profile/friends/decline""[^>]*>.*?name=""user_id"" value=""{incoming}"".*?Decline</button></form>", page.Body);
		Assert.Matches($@"<form method=""post"" action=""/profile/friends/cancel""[^>]*>.*?name=""user_id"" value=""{outgoing}"".*?Cancel request</button></form>", page.Body);
		Assert.Matches($@"<form method=""post"" action=""/profile/friends/remove""[^>]*>.*?name=""user_id"" value=""{friend}"".*?Remove friend</button></form>", page.Body);
		Assert.Contains("action=\"/profile/friends/add\"", page.Body);
		Assert.Contains("name=\"player\"", page.Body);
		Assert.Contains("Send request", page.Body);
		Assert.Contains("Changes here show in the game right away.", page.Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AFriendWhoIsOnlineShowsAsOnlineWithTheirActivity(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Watcher", "watcher@example.org");
		long friend = await AddUser(probe, "Present", 77);
		await Befriend(probe, me.ID, friend);
		Online(friend, "Present", 77);
		var sessions = WebSocketManager.GetUserDataCache().GetOrAdd(EUserSessionType.GameClient, _ => new ConcurrentDictionary<long, UserSession>());
		sessions[friend] = new UserSession(friend, EUserSessionType.GameClient, "genonline_zerohour", "", "", 0, 0);
		try
		{
			PageResponse page = await browser.Get(Friends);
			Assert.Matches(@"Present<span class=""gp-tag"">#0077</span>.*?gp-presence gp-presence-online.*?Online", page.Body);
			Assert.DoesNotContain("Offline", page.Body);
		}
		finally
		{
			sessions.TryRemove(friend, out _);
			Shared().TryRemove(friend, out _);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EmptyListsUseTheEmptyState(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await SignedUp(probe, "Lonely", "lonely@example.org");

		PageResponse friends = await browser.Get(Friends);
		Assert.Contains("class=\"gp-empty\"", friends.Body);
		Assert.Contains("No friends yet", friends.Body);
		Assert.Contains("No requests waiting", friends.Body);
		Assert.DoesNotContain("class=\"gp-count\"", friends.Body);
	}

	// names are stored text: whatever one holds is shown as text on both pages
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AHostileNameIsEncoded(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Reader", "reader@example.org");
		const string hostile = "<img src=x onerror=alert(1)>\"'&";
		long friend = await AddUser(probe, hostile, 1);
		long incoming = await AddUser(probe, "<script>alert(2)</script>", 2);
		await Befriend(probe, me.ID, friend);
		await Request(probe, incoming, me.ID);

		PageResponse friends = await browser.Get(Friends);
		Assert.Equal(200, friends.Status);
		Assert.DoesNotContain("<img src=x", friends.Body);
		Assert.DoesNotContain("<script", friends.Body);
		Assert.DoesNotContain("onerror=alert", friends.Body.Replace("onerror=alert(1)&gt;", String.Empty));
		Assert.Contains("&lt;img src=x onerror=alert(1)&gt;&quot;&#39;&amp;", friends.Body);
		Assert.Contains("&lt;script&gt;alert(2)&lt;/script&gt;", friends.Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheNewClassesAreDefinedAndThePagesAreSelfContained(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Styled", "styledfriends@example.org");
		long friend = await AddUser(probe, "Friendly", 5);
		long incoming = await AddUser(probe, "Asking", 6);
		long outgoing = await AddUser(probe, "Asked", 7);
		await Befriend(probe, me.ID, friend);
		await Request(probe, incoming, me.ID);
		await Request(probe, me.ID, outgoing);
		(AccountsBrowser empty, _) = await SignedUp(probe, "Bare", "bare@example.org");

		string generals = File.ReadAllText(Path.Combine(RepositoryRoot(), "GenOnlineService", "wwwroot", "portal", "generals.css"));
		foreach (PageResponse page in new[] { await browser.Get(Friends), await empty.Get(Friends) })
		{
			Assert.Equal(200, page.Status);
			string[] missing = ClassesIn(page.Body).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => c.StartsWith("gp", StringComparison.Ordinal)).Distinct()
				.Where(c => !Regex.IsMatch(generals, Regex.Escape("." + c) + @"[ :{,>\[.]")).ToArray();
			Assert.True(missing.Length == 0, "not in generals.css: " + String.Join(", ", missing));
			Assert.DoesNotMatch("(src|href|action)=\"(https?:)?//", page.Body);
			Assert.DoesNotContain("<script", page.Body);
			Assert.DoesNotContain("googleapis", page.Body);
			Assert.Equal("DENY", page.FrameOptions);
		}
	}

	// the new rules of the stylesheet use logical properties only and keep the primary controls at 44px
	[Fact]
	public void TheStylesheetUsesLogicalPropertiesAndTouchTargets()
	{
		string generals = File.ReadAllText(Path.Combine(RepositoryRoot(), "GenOnlineService", "wwwroot", "portal", "generals.css"));
		int start = generals.IndexOf("/* friends and blocked players", StringComparison.Ordinal);
		Assert.True(start > 0, "the friends and blocked players rules are missing");
		string mine = generals.Substring(start);
		Assert.DoesNotMatch(@"(^|[\s{;])(margin|padding|border)-(left|right)\s*:", mine);
		Assert.DoesNotMatch(@"(^|[\s{;])(left|right)\s*:", mine);
		Assert.DoesNotMatch(@"text-align:\s*(left|right)", mine);
		Assert.Matches(@"\.gp-more summary\s*\{[^}]*min-height: 44px", mine);
		Assert.DoesNotContain("#", Regex.Replace(mine, @"/\*.*?\*/", String.Empty, RegexOptions.Singleline).Replace("&#", String.Empty));
	}

	// ===== add a friend =====

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AddingByNameAndTagSendsARequestAndComesBackToThePage(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Sender", "sender@example.org");
		long target = await AddUser(probe, "Target", 42);

		// the name in any case
		PageResponse sent = await Post(browser, Friends, "/profile/friends/add", ("player", "  tArGeT#0042 "));
		Assert.Equal(302, sent.Status);
		Assert.Equal("/profile/friends?r=sent", sent.Location);
		Assert.True(await HasRequest(probe, me.ID, target));

		PageResponse page = await browser.Get(sent.Location!);
		Assert.Contains("Friend request sent.", page.Body);
		Assert.Contains("Target<span class=\"gp-tag\">#0042</span>", page.Body);
		Assert.Contains("You sent a request", page.Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AddingByPlayerIdSendsARequest(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "IdSender", "idsender@example.org");
		long target = await AddUser(probe, "ByNumber", 9);

		PageResponse sent = await Post(browser, Friends, "/profile/friends/add", ("player", target.ToString()));
		Assert.Equal("/profile/friends?r=sent", sent.Location);
		Assert.True(await HasRequest(probe, me.ID, target));
	}

	// a name needs its tag, and it must be the whole name: a bare name, a part of a name, a wrong tag, an unknown id, a
	// player who blocked you all end the same way, with the same message and nothing stored
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EveryRefusedAddSaysTheSameThing(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Seeker", "seeker@example.org");
		long target = await AddUser(probe, "Target", 42);
		long twin = await AddUser(probe, "Target", 43);
		long blocker = await AddUser(probe, "Blocker", 5);
		await Block(probe, blocker, me.ID);

		string[] attempts =
		[
			"Target",            // a bare name, though two players have it
			"Targe#0042",        // part of a name
			"Target#0044",       // the wrong tag
			"Targets#0042",      // a longer name
			"#0042",             // no name
			"Target#",           // no tag
			"Target#42x",        // not a tag
			"999999",            // an id nobody has
			"Blocker#0005",      // the player blocked the sender
			blocker.ToString(),  // the same by id
			"",                  // nothing
			"   ",
			new string('x', 5000) + "#0001"
		];
		foreach (string attempt in attempts)
		{
			PageResponse answer = await Post(browser, Friends, "/profile/friends/add", ("player", attempt));
			Assert.Equal(302, answer.Status);
			Assert.Equal("/profile/friends?r=neutral", answer.Location);
		}
		Assert.Contains(Neutral, Decoded(await browser.Get("/profile/friends?r=neutral")));

		await using AppDbContext db = probe.Open();
		Assert.False(await db.FriendRequests.AnyAsync());
		// the tag picks the player when two share a name, and the first one is still found
		Assert.Equal("/profile/friends?r=sent", (await Post(browser, Friends, "/profile/friends/add", ("player", "Target#0043"))).Location);
		Assert.True(await HasRequest(probe, me.ID, twin));
		Assert.False(await HasRequest(probe, me.ID, target));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheNameRulesDecideWhichNamesAreTheSame(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Rules", "rules@example.org");
		// the name key folds case and look-alike characters, as the unique name and tag does: one key and tag is one player
		Assert.Equal(GenOnlineService.Names.NameKey.Of("Paul"), GenOnlineService.Names.NameKey.Of("Pau1"));
		long target = await AddUser(probe, "Paul", 700);

		Assert.Equal("/profile/friends?r=sent", (await Post(browser, Friends, "/profile/friends/add", ("player", "PAUL#0700"))).Location);
		Assert.True(await HasRequest(probe, me.ID, target));
		await Post(browser, Friends, "/profile/friends/cancel", ("user_id", target.ToString()));
		Assert.Equal("/profile/friends?r=sent", (await Post(browser, Friends, "/profile/friends/add", ("player", "Pau1#0700"))).Location);
		Assert.True(await HasRequest(probe, me.ID, target));
		// a different name with that tag is nobody
		Assert.Equal("/profile/friends?r=neutral", (await Post(browser, Friends, "/profile/friends/add", ("player", "Pauline#0700"))).Location);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task YouCannotAddYourself(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Narcissus", "narcissus@example.org");

		Assert.Equal("/profile/friends?r=self", (await Post(browser, Friends, "/profile/friends/add", ("player", Handle(me)))).Location);
		Assert.Equal("/profile/friends?r=self", (await Post(browser, Friends, "/profile/friends/add", ("player", me.ID.ToString()))).Location);
		Assert.Contains("That's you.", Decoded(await browser.Get("/profile/friends?r=self")));
		await using AppDbContext db = probe.Open();
		Assert.False(await db.FriendRequests.AnyAsync());
	}

	// asking twice is one request; asking for someone who already asked you makes you friends
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task DuplicatesAndCrossedRequestsFollowTheGameRules(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Twice", "twice@example.org");
		long target = await AddUser(probe, "Once", 1);
		long asker = await AddUser(probe, "Asker", 2);

		await Post(browser, Friends, "/profile/friends/add", ("player", target.ToString()));
		await Post(browser, Friends, "/profile/friends/add", ("player", target.ToString()));
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(1, await db.FriendRequests.CountAsync(r => r.FromUserId == me.ID));
		}

		await Request(probe, asker, me.ID);
		await Post(browser, Friends, "/profile/friends/add", ("player", asker.ToString()));
		Assert.True(await AreFriends(probe, me.ID, asker));
		Assert.False(await HasRequest(probe, asker, me.ID));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AFullFriendsListSaysSoAndKeepsTheRequestBack(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Popular", "popular@example.org");
		long target = await AddUser(probe, "OneTooMany", 9999);
		for (int i = 0; i < SocialActions.FriendsLimit; i++)
		{
			await Befriend(probe, me.ID, await AddUser(probe, "Friend" + i, i + 1));
		}

		PageResponse answer = await Post(browser, Friends, "/profile/friends/add", ("player", target.ToString()));
		Assert.Equal("/profile/friends?r=full", answer.Location);
		Assert.Contains("the limit is 200 friends", Decoded(await browser.Get(answer.Location!)));
		Assert.False(await HasRequest(probe, me.ID, target));

		// accepting needs room on both sides as well
		long asker = await AddUser(probe, "Asker", 8888);
		await Request(probe, asker, me.ID);
		Assert.Equal("/profile/friends?r=full", (await Post(browser, Friends, "/profile/friends/accept", ("user_id", asker.ToString()))).Location);
		Assert.False(await AreFriends(probe, me.ID, asker));
		Assert.True(await HasRequest(probe, asker, me.ID));
	}

	// ===== accept, decline, cancel, remove =====

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AcceptDeclineCancelAndRemoveChangeTheLists(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Manager", "manager@example.org");
		long accepted = await AddUser(probe, "Accepted", 1);
		long declined = await AddUser(probe, "Declined", 2);
		long cancelled = await AddUser(probe, "Cancelled", 3);
		long removed = await AddUser(probe, "Removed", 4);
		await Request(probe, accepted, me.ID);
		await Request(probe, declined, me.ID);
		await Request(probe, me.ID, cancelled);
		await Befriend(probe, me.ID, removed);

		PageResponse answer = await Post(browser, Friends, "/profile/friends/accept", ("user_id", accepted.ToString()));
		Assert.Equal(302, answer.Status);
		Assert.Equal("/profile/friends?r=accepted", answer.Location);
		Assert.True(await AreFriends(probe, me.ID, accepted));
		Assert.False(await HasRequest(probe, accepted, me.ID));
		Assert.Contains("You are now friends.", (await browser.Get(answer.Location!)).Body);

		answer = await Post(browser, Friends, "/profile/friends/decline", ("user_id", declined.ToString()));
		Assert.Equal("/profile/friends?r=declined", answer.Location);
		Assert.False(await HasRequest(probe, declined, me.ID));
		Assert.False(await AreFriends(probe, me.ID, declined));

		answer = await Post(browser, Friends, "/profile/friends/cancel", ("user_id", cancelled.ToString()));
		Assert.Equal("/profile/friends?r=cancelled", answer.Location);
		Assert.False(await HasRequest(probe, me.ID, cancelled));

		answer = await Post(browser, Friends, "/profile/friends/remove", ("user_id", removed.ToString()));
		Assert.Equal("/profile/friends?r=removed", answer.Location);
		Assert.False(await AreFriends(probe, me.ID, removed));
		Assert.Contains("Friend removed.", (await browser.Get(answer.Location!)).Body);

		// a page that shows an old list answers that it has changed, and changes nothing
		Assert.Equal("/profile/friends?r=gone", (await Post(browser, Friends, "/profile/friends/accept", ("user_id", declined.ToString()))).Location);
		Assert.Equal("/profile/friends?r=gone", (await Post(browser, Friends, "/profile/friends/remove", ("user_id", removed.ToString()))).Location);
		Assert.Equal("/profile/friends?r=gone", (await Post(browser, Friends, "/profile/friends/remove", ("user_id", "not a number"))).Location);
		Assert.Equal("/profile/friends?r=gone", (await Post(browser, Friends, "/profile/friends/decline", ("user_id", "999999"))).Location);
		Assert.Contains("already changed", (await browser.Get("/profile/friends?r=gone")).Body);
	}

	// the message the address carries is one of a fixed set: any other value shows no message and no text of its own
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task OnlyKnownStatusCodesShowAMessage(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await SignedUp(probe, "Reflect", "reflect@example.org");

		PageResponse page = await browser.Get("/profile/friends?r=%3Cb%3Ehello%3C%2Fb%3E");
		Assert.Equal(200, page.Status);
		Assert.DoesNotContain("<b>hello", page.Body);
		Assert.DoesNotContain("class=\"gp-alert", page.Body);
		// a success is told with the status role, a refusal with the alert role
		Assert.Contains("role=\"status\" class=\"gp-alert gp-alert-success\"", (await browser.Get("/profile/friends?r=sent")).Body);
		Assert.Contains("role=\"alert\" class=\"gp-alert\"", (await browser.Get("/profile/friends?r=neutral")).Body);
	}

	// ===== antiforgery and the rate limit =====

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EveryPostNeedsTheFormToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Guarded", "guardedfriends@example.org");
		long other = await AddUser(probe, "Other", 1);
		await Befriend(probe, me.ID, other);
		await Request(probe, other, me.ID);

		foreach ((string action, string field, string value) in new[]
		{
			("/profile/friends/add", "player", "Other#0001"),
			("/profile/friends/accept", "user_id", other.ToString()),
			("/profile/friends/decline", "user_id", other.ToString()),
			("/profile/friends/cancel", "user_id", other.ToString()),
			("/profile/friends/remove", "user_id", other.ToString())
		})
		{
			PageResponse answer = await browser.Post(action, new() { [field] = value });
			Assert.Equal(400, answer.Status);
			Assert.Null(answer.Location);
		}
		Assert.True(await AreFriends(probe, me.ID, other));
		Assert.True(await HasRequest(probe, other, me.ID));
	}

	// adding a friend is limited to 20 tries in 15 minutes for each account (blocking shares the limit); the answer is 429 with the page and a message
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AddingIsLimitedForEachAccount(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User me) = await SignedUp(probe, "Prober", "prober@example.org");
		(AccountsBrowser fresh, User other) = await SignedUp(probe, "Patient", "patient@example.org");
		long target = await AddUser(probe, "Target", 1);

		PageResponse form = await browser.Get(Friends);
		string token = AccountsBrowser.Token(form);
		for (int i = 0; i < 20; i++)
		{
			PageResponse answer = await browser.Post("/profile/friends/add", new() { ["__go_consent_token"] = token, ["player"] = "Nobody#000" + (i % 10) });
			Assert.Equal(302, answer.Status);
		}
		PageResponse limited = await browser.Post("/profile/friends/add", new() { ["__go_consent_token"] = token, ["player"] = target.ToString() });
		Assert.Equal(429, limited.Status);
		Assert.Contains("Too many tries.", limited.Body);
		Assert.Contains("class=\"gp-side\"", limited.Body);
		Assert.False(await HasRequest(probe, me.ID, target));

		// the block in a friend's menu takes any id, so it shares the limit too
		PageResponse limitedBlock = await browser.Post("/profile/friends/block", new() { ["__go_consent_token"] = token, ["user_id"] = target.ToString() });
		Assert.Equal(429, limitedBlock.Status);
		Assert.Contains("Too many tries.", limitedBlock.Body);
		Assert.False(await HasBlock(probe, me.ID, target));

		// the limit is the account's: another account still adds, and the limited one still accepts and removes
		Assert.Equal("/profile/friends?r=sent", (await Post(fresh, Friends, "/profile/friends/add", ("player", target.ToString()))).Location);
		Assert.True(await HasRequest(probe, other.ID, target));
		await Request(probe, target, me.ID);
		Assert.Equal("/profile/friends?r=accepted", (await browser.Post("/profile/friends/accept", new() { ["__go_consent_token"] = token, ["user_id"] = target.ToString() })).Location);
	}

	// ===== the game hears about it =====

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AChangeOnTheWebReachesTheGameLikeOneFromTheGame(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser webA, User a) = await SignedUp(probe, "WebOne", "webone@example.org");
		(AccountsBrowser webB, User b) = await SignedUp(probe, "WebTwo", "webtwo@example.org");
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

			// 1. a request: the other player hears it, the cache of the other player lists it
			await Post(webA, Friends, "/profile/friends/add", ("player", b.ID.ToString()));
			Assert.True((await SocialActions.SendFriendRequestAsync(factory, c, d)).Ok);
			Assert.Contains("FriendRequestReceived", log.Events(b.ID));
			Assert.Equal(new[] { a.ID }, Pending(b.ID));
			Assert.Equal(Normalised(log.Events(b.ID), log.Events(a.ID)), Normalised(log.Events(d), log.Events(c)));
			Assert.Equal(Pending(d).Length, Pending(b.ID).Length);
			log.Clear();

			// 2. accepting: both hear it, both caches list the friendship and no request is left
			await Post(webB, Friends, "/profile/friends/accept", ("user_id", a.ID.ToString()));
			Assert.True((await SocialActions.AcceptFriendRequestAsync(factory, d, c)).Ok);
			Assert.Contains("FriendRequestAccepted", log.Events(a.ID));
			Assert.Contains("FriendPresence", log.Events(a.ID));
			Assert.Contains("FriendPresence", log.Events(b.ID));
			Assert.Equal(new[] { b.ID }, Friends_(a.ID));
			Assert.Equal(new[] { a.ID }, Friends_(b.ID));
			Assert.Empty(Pending(b.ID));
			Assert.Equal(Normalised(log.Events(a.ID), log.Events(b.ID)), Normalised(log.Events(c), log.Events(d)));
			Assert.Equal(Friends_(a.ID).Length, Friends_(c).Length);
			log.Clear();

			// 3. removing a friend: both hear it, both caches drop it
			await Post(webA, Friends, "/profile/friends/remove", ("user_id", b.ID.ToString()));
			Assert.True((await SocialActions.RemoveFriendAsync(factory, c, d)).Ok);
			Assert.Contains("FriendRemoved", log.Events(a.ID));
			Assert.Contains("FriendRemoved", log.Events(b.ID));
			Assert.Empty(Friends_(a.ID));
			Assert.Empty(Friends_(b.ID));
			Assert.Equal(Normalised(log.Events(a.ID), log.Events(b.ID)), Normalised(log.Events(c), log.Events(d)));
			log.Clear();

			// 4. declining and cancelling clear the caches of both players as the game's calls do
			await Post(webA, Friends, "/profile/friends/add", ("player", b.ID.ToString()));
			Assert.Equal(new[] { a.ID }, Pending(b.ID));
			await Post(webB, Friends, "/profile/friends/decline", ("user_id", a.ID.ToString()));
			Assert.Empty(Pending(b.ID));
			await Post(webA, Friends, "/profile/friends/add", ("player", b.ID.ToString()));
			await Post(webA, Friends, "/profile/friends/cancel", ("user_id", b.ID.ToString()));
			Assert.Empty(Pending(b.ID));
			Assert.False(await HasRequest(probe, a.ID, b.ID));
		}
		finally
		{
			foreach (long id in everyone)
			{
				Shared().TryRemove(id, out _);
			}
		}
	}

	// the events of a pair, with the players' ids left out, so the web's pair and the game's pair compare
	internal static string[] Normalised(IEnumerable<string> first, IEnumerable<string> second) =>
		first.Select(e => "first:" + e).Concat(second.Select(e => "second:" + e)).Order(StringComparer.Ordinal).ToArray();

	internal static long[] Pending(long id) => Read(id, c => c.PendingRequests);
	internal static long[] Friends_(long id) => Read(id, c => c.Friends);
	internal static long[] Blocked_(long id) => Read(id, c => c.Blocked);

	internal static long[] Read(long id, Func<UserSocialContainer, HashSet<long>> set)
	{
		UserSocialContainer container = WebSocketManager.GetSharedDataForUser(id)!.GetSocialContainer();
		lock (container)
		{
			return set(container).Order().ToArray();
		}
	}

	internal static ConcurrentDictionary<long, SharedUserData> Shared() =>
		(ConcurrentDictionary<long, SharedUserData>)typeof(WebSocketManager).GetField("m_dictSharedUserData", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

	internal static void Online(long userID, string name, int tag) =>
		Shared()[userID] = new SharedUserData(userID, new UserSocialContainer(), name, tag, new PlayerStats(userID, 1500, 0, 1500));
}

// Records what RealtimeOut sends while a test holds it: the hub of the service is replaced by one that notes the group and the
// event, and ignores everything for any other test (the log is per async flow). The service's own sends need no hub here.
internal sealed class RealtimeLog : IDisposable
{
	private static readonly AsyncLocal<RealtimeLog?> s_current = new();

	static RealtimeLog()
	{
		RealtimeOut.Initialize(new RecordingHub());
	}

	private readonly List<(string Group, string Event)> m_events = new();

	public static RealtimeLog Begin()
	{
		var log = new RealtimeLog();
		s_current.Value = log;
		return log;
	}

	public void Dispose() => s_current.Value = null;

	public void Clear()
	{
		lock (m_events)
		{
			m_events.Clear();
		}
	}

	// the events a user's group was sent, in order
	public List<string> Events(long userID)
	{
		lock (m_events)
		{
			return m_events.Where(e => e.Group == RealtimeGroups.User(userID)).Select(e => e.Event).ToList();
		}
	}

	private static void Record(IEnumerable<string> groups, string name)
	{
		RealtimeLog? log = s_current.Value;
		if (log == null)
		{
			return;
		}
		lock (log.m_events)
		{
			foreach (string group in groups)
			{
				log.m_events.Add((group, name));
			}
		}
	}

	public class Recorder : DispatchProxy
	{
		public IReadOnlyList<string> Groups { get; set; } = [];

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			Record(Groups, targetMethod!.Name);
			return Task.CompletedTask;
		}

		public static IRealtimeClient For(IEnumerable<string> groups)
		{
			IRealtimeClient client = Create<IRealtimeClient, Recorder>();
			((Recorder)(object)client).Groups = groups.ToArray();
			return client;
		}
	}

	private sealed class RecordingHub : IHubContext<RealtimeHub, IRealtimeClient>
	{
		public IHubClients<IRealtimeClient> Clients { get; } = new RecordingClients();

		public IGroupManager Groups { get; } = new NoGroups();
	}

	private sealed class NoGroups : IGroupManager
	{
		public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}

	private sealed class RecordingClients : IHubClients<IRealtimeClient>
	{
		public IRealtimeClient All => Recorder.For([]);

		public IRealtimeClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Recorder.For([]);

		public IRealtimeClient Client(string connectionId) => Recorder.For([]);

		public IRealtimeClient Clients(IReadOnlyList<string> connectionIds) => Recorder.For([]);

		public IRealtimeClient Group(string groupName) => Recorder.For([groupName]);

		public IRealtimeClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Recorder.For([groupName]);

		public IRealtimeClient Groups(IReadOnlyList<string> groupNames) => Recorder.For(groupNames);

		public IRealtimeClient User(string userId) => Recorder.For([]);

		public IRealtimeClient Users(IReadOnlyList<string> userIds) => Recorder.For([]);
	}
}
