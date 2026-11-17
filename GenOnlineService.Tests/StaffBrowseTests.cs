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
using GenOnlineService.Rest;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The staff lists of players and matches open without a search (T17f): GET /api/v1/players/search without a query lists
// every account, most recently signed in first, with filters, sorting and paging in the database; GET /api/v1/matches/search
// lists matches newest first with their filters. Both are staff only, with the sign-in cookie.
[Collection(AdminAreaCollection.Name)]
public class StaffBrowseTests
{
	private sealed record Seeded(User Warden, User Chief, User Alpha, User Bravo, User Charlie, User Delta, User Echo, long M3, long M1, long M2, long M4, AdminBrowser Moderator, AdminBrowser Admin, AdminBrowser Player);

	private static readonly DateTime s_now = DateTime.UtcNow;

	// Two staff members signed in through the pages, five players with their history: Alpha online with three matches and a
	// rating, Bravo blocked and banned, Charlie muted (and a lifted ban), Delta reported and never signed in, Echo with an
	// unreviewed anti-cheat row (a flag only anticheat.review sees) and a dismissed report
	private static async Task<Seeded> Seed(ProbeDatabase probe)
	{
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		User warden = await AdminArea.Grant(probe, "warden@example.org", StaffRoles.Moderator);
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		User chief = await AdminArea.Grant(probe, "chief@example.org", StaffRoles.Admin);
		var player = new AdminBrowser(probe);
		await player.SignUp("Plain", "plain@example.org");

		User alpha = await AddUser(probe, "RedGeneral", 1, s_now.AddDays(-300), s_now.AddHours(-1));
		User bravo = await AddUser(probe, "general_blue", 2, s_now.AddDays(-200), s_now.AddDays(-3), UserStates.Blocked);
		User charlie = await AddUser(probe, "TankRush", 3, s_now.AddDays(-100), s_now.AddDays(-20));
		User delta = await AddUser(probe, "Quiet", 4, s_now.AddDays(-10), null);
		User echo = await AddUser(probe, "Echo", 5, s_now.AddDays(-50), s_now.AddDays(-40));

		long m3, m1, m2, m4;
		await using (AppDbContext db = probe.Open())
		{
			db.Sanctions.AddRange(
				new Sanction { UserID = bravo.ID, Type = SanctionTypes.Ban, Reason = "cheating", CreatedByKind = AuditActorKinds.User, CreatedByID = warden.ID, CreatedAt = s_now.AddDays(-1) },
				new Sanction { UserID = charlie.ID, Type = SanctionTypes.Mute, Reason = "spam", CreatedByKind = AuditActorKinds.User, CreatedByID = warden.ID, CreatedAt = s_now.AddMinutes(-5), ExpiresAt = s_now.AddHours(1) },
				new Sanction { UserID = charlie.ID, Type = SanctionTypes.Ban, Reason = "lifted", CreatedByKind = AuditActorKinds.User, CreatedByID = warden.ID, CreatedAt = s_now.AddDays(-9), LiftedAt = s_now.AddDays(-8) },
				new Sanction { UserID = echo.ID, Type = SanctionTypes.Mute, Reason = "expired", CreatedByKind = AuditActorKinds.User, CreatedByID = warden.ID, CreatedAt = s_now.AddDays(-9), ExpiresAt = s_now.AddDays(-8) });
			db.PlayerReports.AddRange(
				new PlayerReport { ReporterID = alpha.ID, ReportedUserID = delta.ID, Reason = "rude", Evidence = "{\"messages\":[]}", Status = PlayerReportStatuses.Open, CreatedAt = s_now.AddHours(-2) },
				new PlayerReport { ReporterID = alpha.ID, ReportedUserID = echo.ID, Reason = "old", Evidence = "{\"messages\":[]}", Status = PlayerReportStatuses.Dismissed, CreatedAt = s_now.AddDays(-2) });
			db.PlayerRatings.Add(new PlayerRating { UserID = alpha.ID, Kind = RatingKinds.AllTime, Rating = 1650, MatchesPlayed = 3, UpdatedAt = s_now });
			db.PlayerRatings.Add(new PlayerRating { UserID = bravo.ID, Kind = RatingKinds.AllTime, Rating = 1500, MatchesPlayed = 0, UpdatedAt = s_now });

			// in the order they started, so the ids follow the start times as in the service
			global::Match third = AddMatch(db, "Tournament Desert", null, s_now.AddDays(-2), s_now.AddDays(-2).AddMinutes(25),
				(alpha, null, false), (charlie, null, false));
			global::Match first = AddMatch(db, "Tournament Desert", ELobbyType.QuickMatch, s_now.AddHours(-5), s_now.AddHours(-4.5),
				(alpha, MatchResults.Win, false), (bravo, MatchResults.Loss, false));
			global::Match second = AddMatch(db, "Alpine Assault", ELobbyType.CustomGame, s_now.AddHours(-3), s_now.AddHours(-2),
				(alpha, MatchResults.Loss, true), (charlie, MatchResults.Win, false));
			global::Match running = AddMatch(db, "Twilight Flame", ELobbyType.QuickMatch, s_now.AddMinutes(-10), null,
				(bravo, null, false));
			await db.SaveChangesAsync();
			(m3, m1, m2, m4) = (third.Id, first.Id, second.Id, running.Id);

			db.AnticheatModuleReports.Add(new AnticheatModuleReport { UserId = bravo.ID, MatchId = m1, ModuleName = "inject.dll", ModulePath = "C:\\inject.dll", ModuleSize = 1 });
			db.AnticheatProbeReports.Add(new AnticheatProbeReport { UserId = echo.ID, MatchId = 0, Reason = "Spoofed device value" });
			db.AnticheatNewAccountGames.Add(new AnticheatNewAccountGame { UserId = charlie.ID, MatchId = m2, ReviewedAt = s_now });
			await db.SaveChangesAsync();
		}

		return new Seeded(warden, chief, alpha, bravo, charlie, delta, echo, m3, m1, m2, m4, moderator, admin, player);
	}

	private static async Task<User> AddUser(ProbeDatabase probe, string name, int tag, DateTime created, DateTime? signedIn, string state = UserStates.Active)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, State = state, CreatedAt = created, UpdatedAt = created, LastSignInAt = signedIn };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}

	private static global::Match AddMatch(AppDbContext db, string map, ELobbyType? type, DateTime started, DateTime? finished, params (User User, string? Result, bool Desynced)[] players)
	{
		var match = new global::Match { Name = map + " game", LobbyType = type, MapName = map, IsOfficialMap = true, StartedAt = started, FinishedAt = finished };
		short slot = 0;
		foreach ((User user, string? result, bool desynced) in players)
		{
			match.Players.Add(new MatchPlayer
			{
				Slot = slot,
				UserId = user.ID,
				Team = slot,
				Side = 1,
				Color = slot,
				Result = result,
				Details = MatchHistory.WriteDetails(new MatchPlayerDetails { display_name = user.Username, slot_state = EPlayerType.SLOT_PLAYER, desynced = desynced, duration = 600 })
			});
			slot++;
		}
		db.Matches.Add(match);
		return match;
	}

	private static JsonElement[] Rows(PageResponse response, string list)
	{
		Assert.True(response.Status == 200, response.Status + ": " + response.Body);
		return JsonDocument.Parse(response.Body).RootElement.GetProperty(list).EnumerateArray().ToArray();
	}

	private static long[] Ids(PageResponse response, string list) => Rows(response, list).Select(r => r.GetProperty("id").GetInt64()).ToArray();

	private static string? Header(PageResponse response, string name) => response.Headers != null && response.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

	private static string FieldOf(PageResponse response)
	{
		Assert.Equal(400, response.Status);
		JsonElement root = JsonDocument.Parse(response.Body).RootElement;
		Assert.Equal("invalid_argument", root.GetProperty("error").GetString());
		return root.GetProperty("params").GetProperty("field").GetString()!;
	}

	// The online players are this process's realtime sessions; the test adds one and takes it away again
	private static IDisposable Online(long userID, string name)
	{
		var shared = (ConcurrentDictionary<long, SharedUserData>)typeof(WebSocketManager).GetField("m_dictSharedUserData", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
		var social = new UserSocialContainer { Friends = new HashSet<long>(), PendingRequests = new HashSet<long>(), Blocked = new HashSet<long>() };
		shared[userID] = new SharedUserData(userID, social, name, 1, new PlayerStats(userID, 1500, 0, 1500));
		var sessions = WebSocketManager.GetUserDataCache().GetOrAdd(EUserSessionType.GameClient, _ => new ConcurrentDictionary<long, UserSession>());
		sessions[userID] = new UserSession(userID, EUserSessionType.GameClient, "genonline", "", "", 0, 0);
		return new Undo(() =>
		{
			sessions.TryRemove(userID, out _);
			shared.TryRemove(userID, out _);
		});
	}

	private sealed class Undo(Action action) : IDisposable
	{
		public void Dispose() => action();
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PlayersListWithoutAQueryMostRecentlySignedInFirst(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);

		using (Online(s.Alpha.ID, "RedGeneral"))
		{
			PageResponse all = await s.Moderator.Get("/api/v1/players/search");
			long[] seeded = [s.Alpha.ID, s.Bravo.ID, s.Charlie.ID, s.Echo.ID, s.Delta.ID];
			// accounts that never signed in (Delta, and the three that signed up with a password) come last, newest account first
			Assert.Equal(seeded, Ids(all, "players").Where(seeded.Contains));
			DateTime?[] signedIn = Rows(all, "players").Select(r => r.GetProperty("last_sign_in_at").ValueKind == JsonValueKind.Null ? (DateTime?)null : r.GetProperty("last_sign_in_at").GetDateTime()).ToArray();
			Assert.Equal(signedIn.Where(t => t != null).OrderByDescending(t => t), signedIn.TakeWhile(t => t != null));
			Assert.All(signedIn.SkipWhile(t => t != null), t => Assert.Null(t));
			Assert.Equal(s.Delta.ID, Ids(all, "players")[4]);
			Assert.Equal("8", Header(all, "X-Total"));

			// an empty query lists the same as none
			Assert.Equal(Ids(all, "players"), Ids(await s.Moderator.Get("/api/v1/players/search?query="), "players"));

			JsonElement alpha = Rows(all, "players").Single(r => r.GetProperty("id").GetInt64() == s.Alpha.ID);
			Assert.Equal("RedGeneral", alpha.GetProperty("name").GetString());
			Assert.Equal("0001", alpha.GetProperty("tag").GetString());
			Assert.True(alpha.GetProperty("online").GetBoolean());
			Assert.Equal(3, alpha.GetProperty("matches").GetInt32());
			Assert.Equal(1650, alpha.GetProperty("rating").GetInt32());
			Assert.False(alpha.GetProperty("banned").GetBoolean());
			Assert.Empty(alpha.GetProperty("roles").EnumerateArray());

			JsonElement bravo = Rows(all, "players").Single(r => r.GetProperty("id").GetInt64() == s.Bravo.ID);
			Assert.Equal(UserStates.Blocked, bravo.GetProperty("state").GetString());
			Assert.True(bravo.GetProperty("banned").GetBoolean());
			Assert.False(bravo.GetProperty("online").GetBoolean());
			Assert.Equal(2, bravo.GetProperty("matches").GetInt32());
			// a rating row without a rated match is no rating
			Assert.Equal(JsonValueKind.Null, bravo.GetProperty("rating").ValueKind);

			JsonElement charlie = Rows(all, "players").Single(r => r.GetProperty("id").GetInt64() == s.Charlie.ID);
			Assert.True(charlie.GetProperty("muted").GetBoolean());
			Assert.False(charlie.GetProperty("banned").GetBoolean());

			JsonElement warden = Rows(all, "players").Single(r => r.GetProperty("id").GetInt64() == s.Warden.ID);
			Assert.Equal([StaffRoles.Moderator], warden.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PlayersFilterByStatusFlagsAndSignIn(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);

		using (Online(s.Alpha.ID, "RedGeneral"))
		{
			Assert.Equal([s.Alpha.ID], Ids(await s.Moderator.Get("/api/v1/players/search?filter=online"), "players"));
		}
		Assert.Empty(Ids(await s.Moderator.Get("/api/v1/players/search?filter=online"), "players"));

		// active sanctions only: the lifted ban and the expired mute do not count
		Assert.Equal([s.Bravo.ID], Ids(await s.Moderator.Get("/api/v1/players/search?filter=banned"), "players"));
		Assert.Equal([s.Charlie.ID], Ids(await s.Moderator.Get("/api/v1/players/search?filter=muted"), "players"));
		Assert.Equal([s.Warden.ID, s.Chief.ID], Ids(await s.Moderator.Get("/api/v1/players/search?filter=staff&order_by=name&sort=desc"), "players"));

		// flagged for review: an open report for everyone, an unreviewed anti-cheat row only for anticheat.review
		PageResponse moderatorFlagged = await s.Moderator.Get("/api/v1/players/search?filter=flagged");
		Assert.Equal([s.Delta.ID], Ids(moderatorFlagged, "players"));
		Assert.True(Rows(moderatorFlagged, "players")[0].GetProperty("flagged").GetBoolean());
		Assert.Equal([s.Bravo.ID, s.Echo.ID, s.Delta.ID], Ids(await s.Admin.Get("/api/v1/players/search?filter=flagged"), "players"));
		Assert.False(Rows(await s.Moderator.Get("/api/v1/players/search?query=Echo"), "players")[0].GetProperty("flagged").GetBoolean());
		Assert.True(Rows(await s.Admin.Get("/api/v1/players/search?query=Echo"), "players")[0].GetProperty("flagged").GetBoolean());

		// filters combine: every one given must hold
		Assert.Equal([s.Bravo.ID], Ids(await s.Admin.Get("/api/v1/players/search?filter=flagged,banned"), "players"));
		Assert.Empty(Ids(await s.Admin.Get("/api/v1/players/search?filter=muted,banned"), "players"));

		// signed in within a window, with a search narrowing it
		long[] day = Ids(await s.Moderator.Get("/api/v1/players/search?signed_in_within=24h"), "players");
		Assert.Contains(s.Alpha.ID, day);
		Assert.DoesNotContain(s.Bravo.ID, day);
		// a search keeps its own order: by name
		Assert.Equal([s.Bravo.ID, s.Alpha.ID], Ids(await s.Moderator.Get("/api/v1/players/search?signed_in_within=7d&query=general"), "players"));
		Assert.Equal([s.Alpha.ID, s.Bravo.ID, s.Charlie.ID], Ids(await s.Moderator.Get("/api/v1/players/search?signed_in_within=30d&order_by=created_at&sort=asc&filter="), "players"));

		Assert.Equal("filter", FieldOf(await s.Moderator.Get("/api/v1/players/search?filter=online,nobody")));
		Assert.Equal("signed_in_within", FieldOf(await s.Moderator.Get("/api/v1/players/search?signed_in_within=1y")));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PlayersSortByEveryColumnBothWays(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);
		long[] seeded = [s.Alpha.ID, s.Bravo.ID, s.Charlie.ID, s.Delta.ID, s.Echo.ID];
		async Task<long[]> Order(string query) => Ids(await s.Moderator.Get("/api/v1/players/search?" + query), "players").Where(seeded.Contains).ToArray();

		Assert.Equal([s.Alpha.ID, s.Bravo.ID, s.Charlie.ID, s.Echo.ID, s.Delta.ID], await Order("order_by=last_sign_in_at"));
		// never signed in stays last both ways
		Assert.Equal([s.Echo.ID, s.Charlie.ID, s.Bravo.ID, s.Alpha.ID, s.Delta.ID], await Order("order_by=last_sign_in_at&sort=asc"));
		Assert.Equal([s.Delta.ID, s.Echo.ID, s.Charlie.ID, s.Bravo.ID, s.Alpha.ID], await Order("order_by=created_at"));
		Assert.Equal([s.Alpha.ID, s.Bravo.ID, s.Charlie.ID, s.Echo.ID, s.Delta.ID], await Order("order_by=created_at&sort=asc"));
		// names in any case: Echo, general_blue, Quiet, RedGeneral, TankRush
		Assert.Equal([s.Echo.ID, s.Bravo.ID, s.Delta.ID, s.Alpha.ID, s.Charlie.ID], await Order("order_by=name"));
		Assert.Equal([s.Charlie.ID, s.Alpha.ID, s.Delta.ID, s.Bravo.ID, s.Echo.ID], await Order("order_by=name&sort=desc"));
		// most matches first, ties by the newer account
		Assert.Equal([s.Alpha.ID, s.Charlie.ID, s.Bravo.ID, s.Echo.ID, s.Delta.ID], await Order("order_by=matches"));
		Assert.Equal([s.Delta.ID, s.Echo.ID, s.Bravo.ID, s.Charlie.ID, s.Alpha.ID], await Order("order_by=matches&sort=asc"));

		Assert.Equal("order_by", FieldOf(await s.Moderator.Get("/api/v1/players/search?order_by=email")));
		Assert.Equal("sort", FieldOf(await s.Moderator.Get("/api/v1/players/search?order_by=name&sort=up")));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PlayersPageInTheDatabaseUpToAHundred(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);

		PageResponse first = await s.Moderator.Get("/api/v1/players/search?order_by=created_at&sort=asc&per_page=3");
		Assert.Equal([s.Alpha.ID, s.Bravo.ID, s.Charlie.ID], Ids(first, "players"));
		Assert.Equal("8", Header(first, "X-Total"));
		Assert.Equal("2", Header(first, "X-Next-Page"));
		PageResponse last = await s.Moderator.Get("/api/v1/players/search?order_by=created_at&sort=asc&per_page=3&page=3");
		Assert.Equal(2, Ids(last, "players").Length);
		Assert.Equal("", Header(last, "X-Next-Page"));
		Assert.Empty(Ids(await s.Moderator.Get("/api/v1/players/search?per_page=3&page=4"), "players"));

		Assert.Equal(200, (await s.Moderator.Get("/api/v1/players/search?per_page=100")).Status);
		Assert.Equal("per_page", FieldOf(await s.Moderator.Get("/api/v1/players/search?per_page=101")));
		Assert.Equal("per_page", FieldOf(await s.Moderator.Get("/api/v1/players/search?per_page=0")));
		Assert.Equal("page", FieldOf(await s.Moderator.Get("/api/v1/players/search?page=0")));
	}

	// The search of before still answers the same: a name part anywhere in any case or the exact id first, by name, and
	// a term of one character refused; a #tag now narrows a name to that tag
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PlayersSearchKeepsItsOrderAndRules(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);

		PageResponse general = await s.Moderator.Get("/api/v1/players/search?query=GENERAL");
		Assert.Equal([s.Bravo.ID, s.Alpha.ID], Ids(general, "players"));
		Assert.Equal("2", Header(general, "X-Total"));
		JsonElement row = Rows(general, "players")[0];
		Assert.Equal(["id", "name", "tag", "state", "created_at", "last_sign_in_at"], row.EnumerateObject().Select(p => p.Name).Take(6));

		Assert.Equal([s.Charlie.ID], Ids(await s.Moderator.Get($"/api/v1/players/search?query={s.Charlie.ID}"), "players"));
		Assert.Equal("query", FieldOf(await s.Moderator.Get("/api/v1/players/search?query=a")));
		Assert.Equal("query", FieldOf(await s.Moderator.Get("/api/v1/players/search?query=" + new string('x', 33))));

		Assert.Equal([s.Alpha.ID], Ids(await s.Moderator.Get("/api/v1/players/search?query=" + Uri.EscapeDataString("redgeneral#0001")), "players"));
		Assert.Equal([s.Alpha.ID], Ids(await s.Moderator.Get("/api/v1/players/search?query=" + Uri.EscapeDataString("General#1")), "players"));
		Assert.Empty(Ids(await s.Moderator.Get("/api/v1/players/search?query=" + Uri.EscapeDataString("RedGeneral#0002")), "players"));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task MatchesListNewestFirstWithPlayersAndFlags(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);

		PageResponse all = await s.Moderator.Get("/api/v1/matches/search");
		Assert.Equal([s.M4, s.M2, s.M1, s.M3], Ids(all, "matches"));
		Assert.Equal("4", Header(all, "X-Total"));

		JsonElement[] rows = Rows(all, "matches");
		JsonElement running = rows[0];
		Assert.Equal("quick_match", running.GetProperty("type").GetString());
		Assert.Equal(JsonValueKind.Null, running.GetProperty("finished_at").ValueKind);
		Assert.Equal(JsonValueKind.Null, running.GetProperty("duration_s").ValueKind);
		Assert.Empty(running.GetProperty("flags").EnumerateArray());

		JsonElement desynced = rows[1];
		Assert.Equal("custom", desynced.GetProperty("type").GetString());
		Assert.Equal("Alpine Assault", desynced.GetProperty("map").GetProperty("name").GetString());
		Assert.Equal(3600, desynced.GetProperty("duration_s").GetInt64());
		Assert.Equal(["desync"], desynced.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
		JsonElement[] players = desynced.GetProperty("players").EnumerateArray().ToArray();
		Assert.Equal([s.Alpha.ID, s.Charlie.ID], players.Select(p => p.GetProperty("user_id").GetInt64()));
		Assert.Equal("RedGeneral", players[0].GetProperty("name").GetString());
		Assert.Equal("0001", players[0].GetProperty("tag").GetString());
		Assert.True(players[0].GetProperty("desynced").GetBoolean());
		Assert.False(players[0].GetProperty("won").GetBoolean());
		Assert.True(players[1].GetProperty("won").GetBoolean());

		// the anti-cheat row of the quick match is a flag only anticheat.review sees
		Assert.Empty(rows[2].GetProperty("flags").EnumerateArray());
		Assert.Equal(["anticheat"], Rows(await s.Admin.Get("/api/v1/matches/search"), "matches")[2].GetProperty("flags").EnumerateArray().Select(f => f.GetString()));

		// finished without a winner, and a lobby type the old history did not keep
		Assert.Equal(["no_result"], rows[3].GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
		Assert.Equal(JsonValueKind.Null, rows[3].GetProperty("type").ValueKind);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task MatchesFilterByKindMapPlayerDatesAndFlags(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);
		async Task<long[]> Matches(AdminBrowser browser, string query) => Ids(await browser.Get("/api/v1/matches/search?" + query), "matches");

		Assert.Equal([s.M4, s.M1], await Matches(s.Moderator, "type=quick_match"));
		Assert.Equal([s.M2], await Matches(s.Moderator, "type=custom"));
		Assert.Equal([s.M1, s.M3], await Matches(s.Moderator, "map=desert"));
		Assert.Equal([s.M2, s.M3], await Matches(s.Moderator, $"player={s.Charlie.ID}"));
		Assert.Equal([s.M4, s.M1], await Matches(s.Moderator, "player=" + Uri.EscapeDataString("General_Blue#0002")));
		Assert.Equal([s.M2, s.M1, s.M3], await Matches(s.Moderator, "player=redgeneral"));
		Assert.Empty(await Matches(s.Moderator, "player=nobody"));

		string yesterday = s_now.AddDays(-1).ToString("yyyy-MM-dd");
		string twoDaysAgo = s_now.AddDays(-2).ToString("yyyy-MM-dd");
		Assert.Equal([s.M4, s.M2, s.M1], await Matches(s.Moderator, "from=" + s_now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ")));
		Assert.Equal([s.M3], await Matches(s.Moderator, $"from={twoDaysAgo}&to={twoDaysAgo}"));
		Assert.Contains(s.M3, await Matches(s.Moderator, $"to={yesterday}"));

		// flagged: a desync or no result for everyone, an unreviewed anti-cheat row with anticheat.review
		Assert.Equal([s.M2, s.M3], await Matches(s.Moderator, "flagged=true"));
		Assert.Equal([s.M2, s.M1, s.M3], await Matches(s.Admin, "flagged=true"));
		Assert.Equal([s.M4, s.M2, s.M1, s.M3], await Matches(s.Moderator, "flagged=false"));
		Assert.Equal([s.M1], await Matches(s.Admin, "flagged=true&type=quick_match&map=desert"));

		Assert.Equal("type", FieldOf(await s.Moderator.Get("/api/v1/matches/search?type=ranked")));
		Assert.Equal("from", FieldOf(await s.Moderator.Get("/api/v1/matches/search?from=yesterday")));
		Assert.Equal("to", FieldOf(await s.Moderator.Get("/api/v1/matches/search?to=2026-13-01")));
		Assert.Equal("flagged", FieldOf(await s.Moderator.Get("/api/v1/matches/search?flagged=yes")));
		Assert.Equal("map", FieldOf(await s.Moderator.Get("/api/v1/matches/search?map=" + new string('m', 129))));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task MatchesPageInTheDatabaseUpToAHundred(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);

		PageResponse first = await s.Moderator.Get("/api/v1/matches/search?per_page=3");
		Assert.Equal([s.M4, s.M2, s.M1], Ids(first, "matches"));
		Assert.Equal("4", Header(first, "X-Total"));
		Assert.Equal("2", Header(first, "X-Next-Page"));
		PageResponse second = await s.Moderator.Get("/api/v1/matches/search?per_page=3&page=2");
		Assert.Equal([s.M3], Ids(second, "matches"));
		Assert.Equal("", Header(second, "X-Next-Page"));

		Assert.Equal(200, (await s.Moderator.Get("/api/v1/matches/search?per_page=100")).Status);
		Assert.Equal("per_page", FieldOf(await s.Moderator.Get("/api/v1/matches/search?per_page=101")));
		Assert.Equal("page", FieldOf(await s.Moderator.Get("/api/v1/matches/search?page=0")));
	}

	// A long history is not counted to the end: past the cap the answer has no X-Total and still says whether a next page exists
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task MatchesStopCountingPastTheCap(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);
		await using AppDbContext db = probe.Open();

		StaffMatchList capped = await StaffMatchLists.ListAsync(db, new StaffMatchQuery(), 0, 2, totalCap: 3);
		Assert.Null(capped.Total);
		Assert.True(capped.HasNext);
		Assert.Equal([s.M4, s.M2], capped.Matches.Select(m => m.Id));

		StaffMatchList counted = await StaffMatchLists.ListAsync(db, new StaffMatchQuery(), 2, 2, totalCap: 4);
		Assert.Equal(4, counted.Total);
		Assert.False(counted.HasNext);

		StaffMatchList lastCapped = await StaffMatchLists.ListAsync(db, new StaffMatchQuery(), 2, 2, totalCap: 3);
		Assert.Null(lastCapped.Total);
		Assert.False(lastCapped.HasNext);
	}

	// Both lists are staff only through the cookie rules of T16: signed out 401, a player 403, a foreign Origin 403; a GET needs
	// no antiforgery header
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheListsAreStaffOnlyWithTheCookieRules(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Seeded s = await Seed(probe);

		foreach (string path in new[] { "/api/v1/players/search", "/api/v1/players/search?filter=online", "/api/v1/matches/search", "/api/v1/matches/search?flagged=true" })
		{
			PageResponse signedOut = await new AdminBrowser(probe).Get(path);
			Assert.Equal(401, signedOut.Status);
			Assert.Equal("unauthorized", AdminArea.ErrorOf(signedOut));

			PageResponse player = await s.Player.Get(path);
			Assert.Equal(403, player.Status);
			Assert.Equal("forbidden", AdminArea.ErrorOf(player));

			Assert.Equal(403, (await s.Moderator.Get(path, new() { ["Origin"] = "https://evil.example" })).Status);
			Assert.Equal(200, (await s.Moderator.Get(path, new() { ["Origin"] = "http://" + AdminBrowser.Host })).Status);
			Assert.Equal(200, (await s.Moderator.Get(path)).Status);
		}
	}
}
