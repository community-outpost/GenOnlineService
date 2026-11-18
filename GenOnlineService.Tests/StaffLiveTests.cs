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
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The live operations of the staff pages (T3): the realtime status, every live lobby and the connection reports of a
// player or a lobby, read through the sign-in cookie as the realtime and connections CLI commands show them
[Collection(AdminAreaCollection.Name)]
public class StaffLiveTests
{
	private static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role, LobbyManager? lobbies = null)
	{
		var browser = new AdminBrowser(probe, services => { if (lobbies != null) services.AddSingleton(lobbies); });
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role));
	}

	private static async Task<(AdminBrowser Browser, User User)> PlayerAsync(ProbeDatabase probe, string name)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, (await PasswordPagesTests.UserByEmail(probe, email))!);
	}

	private static async Task<List<AuditLogEntry>> AuditOf(ProbeDatabase probe, string action)
	{
		await using AppDbContext db = probe.Open();
		return await db.AuditLog.AsNoTracking().Where(e => e.Action == action).OrderBy(e => e.ID).ToListAsync();
	}

	private static JsonElement Json(PageResponse response) => JsonDocument.Parse(response.Body).RootElement;

	// The status

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAdminReadsTheRealtimeStatusAsTheCliShowsIt(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin, NewLobbies(SetupLobby(1, 11, 12)));
		bool postgres;
		RealtimeLease? lease = null;
		DateTime now = DateTime.UtcNow;
		await using (AppDbContext db = probe.Open())
		{
			postgres = db.Database.IsNpgsql();
		}
		// the lease reads need PostgreSQL, as the CLI
		if (postgres)
		{
			await using AppDbContext db = probe.Open();
			now = await Database.Lease.DatabaseNow(db);
			await db.RealtimeLeases.Where(l => l.ID == Database.Lease.RowID).ExecuteUpdateAsync(set => set
				.SetProperty(l => l.Holder, "copy-a-1234")
				.SetProperty(l => l.Epoch, 7)
				.SetProperty(l => l.ExpiresAt, now.AddSeconds(30))
				.SetProperty(l => l.Standby, "copy-b-5678")
				.SetProperty(l => l.StandbyExpiresAt, now.AddSeconds(30))
				.SetProperty(l => l.HandoverRequestedAt, now));
			lease = await Database.Lease.Read(db);
		}

		PageResponse response = await admin.Get("/api/v1/staff/realtime");
		Assert.Equal(200, response.Status);
		JsonElement status = Json(response);
		Assert.Equal(CopyIdentity.ID, status.GetProperty("copy").GetProperty("id").GetString());
		Assert.True(status.GetProperty("copy").GetProperty("serving").GetBoolean());
		Assert.Equal(1, status.GetProperty("lobbies_open").GetInt32());
		Assert.True(status.GetProperty("players_online").GetInt32() >= 0);
		Assert.Equal(MatchmakingManager.GetPlaylists().Keys.Select(k => (int)k).Order(), status.GetProperty("queues").EnumerateArray().Select(q => q.GetProperty("playlist_id").GetInt32()));
		Assert.Equal(MatchmakingManager.GetPlaylists().Values.Select(p => p.Name).Order(), status.GetProperty("queues").EnumerateArray().Select(q => q.GetProperty("name").GetString()!).Order());

		// the lease as realtime status prints it, PostgreSQL only as the CLI
		if (!postgres)
		{
			Assert.Equal(JsonValueKind.Null, status.GetProperty("lease").ValueKind);
			return;
		}
		JsonElement shown = status.GetProperty("lease");
		Assert.Equal("copy-a-1234", shown.GetProperty("holder").GetString());
		Assert.Equal(7, shown.GetProperty("epoch").GetInt64());
		Assert.Equal("copy-b-5678", shown.GetProperty("standby").GetString());
		Assert.True(shown.GetProperty("handover_requested").GetBoolean());
		long expiresIn = shown.GetProperty("expires_in_ms").GetInt64();
		Assert.InRange(expiresIn, 1, 30000);
		string cli = ModerationCli.DescribeLease(lease!, now);
		Assert.StartsWith("holder copy-a-1234  epoch 7  expires in ", cli);
		Assert.EndsWith("  standby copy-b-5678  handover requested", cli);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheRealtimeStatusNeedsServerStats(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);

		PageResponse refused = await moderator.Get("/api/v1/staff/realtime");
		Assert.Equal(403, refused.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
		Assert.Equal(401, (await new AdminBrowser(probe).Get("/api/v1/staff/realtime")).Status);
	}

	// The CLI's lines stay as they were: an expired lease without a holder, and a live one with a standby
	[Fact]
	public void TheCliDescribesTheLeaseAsBefore()
	{
		DateTime now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
		Assert.Equal("holder none  epoch 0  expired  standby none", ModerationCli.DescribeLease(new RealtimeLease { Holder = "", Epoch = 0, ExpiresAt = DateTime.UnixEpoch }, now));
		Assert.Equal("holder copy-a  epoch 7  expires in 1500 ms  standby copy-b  handover requested", ModerationCli.DescribeLease(new RealtimeLease
		{
			Holder = "copy-a",
			Epoch = 7,
			ExpiresAt = now.AddMilliseconds(1500),
			Standby = "copy-b",
			StandbyExpiresAt = now.AddSeconds(1),
			HandoverRequestedAt = now
		}, now));
		// a standby whose own lease ran out is none
		Assert.Equal("holder copy-a  epoch 3  expires in 2 ms  standby none", ModerationCli.DescribeLease(new RealtimeLease
		{
			Holder = "copy-a",
			Epoch = 3,
			ExpiresAt = now.AddTicks(15000),
			Standby = "copy-b",
			StandbyExpiresAt = now.AddSeconds(-1)
		}, now));
	}

	// The lobbies

	private static LobbyLive SetupLobby(long id, long host, long guest) => new()
	{
		Id = id,
		Type = (int)ELobbyType.CustomGame,
		State = (int)ELobbyState.GAME_SETUP,
		Owner = host,
		Name = "lobby " + id,
		MapName = "Tournament Desert",
		MapPath = "maps/tournament desert/tournament desert",
		MapOfficial = true,
		CreatedAt = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc),
		StartingCash = 10000,
		Build = new LiveBuild { Game = "genonline_zerohour", ExeCrc = 1234567890, IniCrc = 987654321, AnticheatId = -1 },
		Region = "eu",
		MaxCameraHeight = 310,
		Joinability = (int)ELobbyJoinability.Public,
		Slots = new List<LobbyMemberLive>
		{
			new() { SlotIndex = 0, SlotState = (int)EPlayerType.SLOT_PLAYER, UserId = host, DisplayName = "host" + host, NameNotDeduped = "host" + host, NameTag = 1001, Ready = true, HasMap = true, Region = "Europe" },
			new() { SlotIndex = 1, SlotState = (int)EPlayerType.SLOT_PLAYER, UserId = guest, DisplayName = "guest" + guest, NameNotDeduped = "guest" + guest, NameTag = 1002, HasMap = true, Region = "Europe" }
		}.Concat(Enumerable.Range(2, 6).Select(slot => new LobbyMemberLive { SlotIndex = slot, SlotState = (int)EPlayerType.SLOT_OPEN })).ToList(),
		EverMembers = new List<long> { host, guest }
	};

	private static LobbyManager NewLobbies(params LobbyLive[] lobbies)
	{
		var manager = new LobbyManager(null!, null!, null!, null!, NullLogger<LobbyManager>.Instance);
		Dictionary<string, string> mirror = lobbies.ToDictionary(l => LiveKeys.Lobby(l.Id), l => LiveJson.Envelope(LiveKeys.Lobby(l.Id), "a", 1, 1, LiveJson.Data(l)));
		LiveRestoreResult restored = LiveStateTakeover.Restore(mirror, manager, 30_000);
		Assert.Equal(lobbies.Length, restored.Restored);
		return manager;
	}

	// a friends-only lobby with a password, a running match and an open public lobby
	private static LobbyManager ThreeLobbies() => NewLobbies(
		SetupLobby(1, 11, 12) with { Joinability = (int)ELobbyJoinability.FriendsOnly, Passworded = true, Password = "secret-word" },
		SetupLobby(2, 21, 22) with { State = (int)ELobbyState.INGAME, MatchId = 5 },
		SetupLobby(3, 31, 32));

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffSeeEveryLiveLobbyWithItsMembersAndNoPassword(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		LobbyManager lobbies = ThreeLobbies();
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator, lobbies);

		PageResponse response = await moderator.Get("/api/v1/staff/lobbies");
		Assert.Equal(200, response.Status);
		Assert.DoesNotContain("secret-word", response.Body);
		JsonElement[] listed = Json(response).GetProperty("lobbies").EnumerateArray().ToArray();
		Assert.Equal(new long[] { 1, 2, 3 }, listed.Select(l => l.GetProperty("id").GetInt64()));
		Assert.Equal("friends", listed[0].GetProperty("visibility").GetString());
		Assert.True(listed[0].GetProperty("has_password").GetBoolean());
		Assert.Equal("in_game", listed[1].GetProperty("state").GetString());
		Assert.Equal(5, listed[1].GetProperty("match_id").GetInt64());
		JsonElement[] slots = listed[2].GetProperty("slots").EnumerateArray().Where(s => s.TryGetProperty("member", out _)).ToArray();
		Assert.Equal(new long[] { 31, 32 }, slots.Select(s => s.GetProperty("member").GetProperty("id").GetInt64()));
		Assert.Equal("host31", slots[0].GetProperty("member").GetProperty("name").GetString());
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheLiveLobbiesAreForStaffOnly(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		LobbyManager lobbies = ThreeLobbies();
		var player = new AdminBrowser(probe, services => services.AddSingleton(lobbies));
		await player.SignUp("Plain", "plain@example.org");

		PageResponse refused = await player.Get("/api/v1/staff/lobbies");
		Assert.Equal(403, refused.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
		Assert.Equal(401, (await new AdminBrowser(probe, services => services.AddSingleton(lobbies)).Get("/api/v1/staff/lobbies")).Status);
	}

	// GET /api/v1/lobbies is unchanged: a caller without a game session sees the public lobbies in setup only
	[Fact]
	public void ThePlayerLobbyListIsUnchanged()
	{
		var controller = new LobbiesController(ThreeLobbies())
		{
			ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) } }
		};

		var result = Assert.IsType<JsonResult>(controller.List());
		var list = Assert.IsType<LobbyListResponse>(result.Value);
		Assert.Equal(new long[] { 3 }, list.Lobbies.Select(l => l.Id));
	}

	// The connection reports

	private static ConnectionReport Report(long lobby, long reporter, long target, int attempt, string outcome, DateTime updated) => new()
	{
		LobbyID = lobby,
		ReporterID = reporter,
		TargetUserID = target,
		Attempt = attempt,
		Outcome = outcome,
		Transport = outcome == ConnectionReportOutcomes.ConnectedRelay ? "relay" : "direct",
		IpVersion = 4,
		Events = Database.ConnectionReports.SerializeEvents([new("connecting", 0), new(outcome, 4200)]),
		CreatedAt = updated,
		UpdatedAt = updated
	};

	// two attempts between Alpha and Bravo in lobby 501, one from Alpha to Charlie in lobby 502
	private static async Task<(User Alpha, User Bravo, User Charlie)> SeedReportsAsync(ProbeDatabase probe)
	{
		(_, User alpha) = await PlayerAsync(probe, "Alpha");
		(_, User bravo) = await PlayerAsync(probe, "Bravo");
		(_, User charlie) = await PlayerAsync(probe, "Charlie");
		DateTime start = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
		await using AppDbContext db = probe.Open();
		db.ConnectionReports.AddRange(
			Report(501, alpha.ID, bravo.ID, 1, ConnectionReportOutcomes.Failed, start),
			Report(501, bravo.ID, alpha.ID, 1, ConnectionReportOutcomes.ConnectedDirect, start.AddSeconds(1)),
			Report(502, alpha.ID, charlie.ID, 1, ConnectionReportOutcomes.Failed, start.AddSeconds(2)),
			Report(501, alpha.ID, bravo.ID, 2, ConnectionReportOutcomes.ConnectedRelay, start.AddSeconds(3)));
		await db.SaveChangesAsync();
		return (alpha, bravo, charlie);
	}

	private static (long Lobby, long Reporter, long Target, int Attempt) Key(JsonElement row) =>
		(row.GetProperty("lobby_id").GetInt64(), row.GetProperty("reporter").GetProperty("id").GetInt64(), row.GetProperty("target").GetProperty("id").GetInt64(), row.GetProperty("attempt").GetInt32());

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffReadTheConnectionReportsOfAPlayerAsTheCliShowsThemAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(User alpha, User bravo, User charlie) = await SeedReportsAsync(probe);
		(AdminBrowser moderator, User warden) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);

		PageResponse response = await moderator.Get($"/api/v1/players/{alpha.ID}/connection_reports");
		Assert.Equal(200, response.Status);
		JsonElement[] rows = Json(response).GetProperty("reports").EnumerateArray().ToArray();

		// the rows of connections player, newest first
		List<ConnectionReport> cli;
		await using (AppDbContext db = probe.Open())
		{
			cli = await Database.ConnectionReports.GetByPlayer(db, alpha.ID);
		}
		Assert.Equal(cli.Select(r => (r.LobbyID, r.ReporterID, r.TargetUserID, r.Attempt)), rows.Select(Key));
		Assert.Equal(new[] { (501L, alpha.ID, bravo.ID, 2), (502L, alpha.ID, charlie.ID, 1), (501L, bravo.ID, alpha.ID, 1), (501L, alpha.ID, bravo.ID, 1) }, rows.Select(Key));
		JsonElement first = rows[0];
		Assert.Equal("Alpha", first.GetProperty("reporter").GetProperty("name").GetString());
		Assert.Equal("connected_relay", first.GetProperty("outcome").GetString());
		Assert.Equal("relay", first.GetProperty("transport").GetString());
		Assert.Equal(4, first.GetProperty("ip_version").GetInt32());
		Assert.Equal(new[] { ("connecting", 0L), ("connected_relay", 4200L) }, first.GetProperty("events").EnumerateArray().Select(e => (e.GetProperty("state").GetString()!, e.GetProperty("t_ms").GetInt64())));

		// audited as connections player, once an hour per staff member and player
		Assert.Equal(200, (await moderator.Get($"/api/v1/players/{alpha.ID}/connection_reports")).Status);
		AuditLogEntry entry = Assert.Single(await AuditOf(probe, ModerationActions.ConnectionLookup));
		Assert.Equal((AuditActorKinds.User, (long?)warden.ID, AuditTargetKinds.User, (long?)alpha.ID), (entry.ActorKind, entry.ActorID, entry.TargetKind, entry.TargetID));
		Assert.Equal(ModerationSources.Api, entry.Source);
		Assert.Equal("player", JsonDocument.Parse(entry.Details!).RootElement.GetProperty("by").GetString());

		Assert.Equal(404, (await moderator.Get("/api/v1/players/999999/connection_reports")).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffReadTheConnectionReportsOfALobbyPairByPairAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(User alpha, User bravo, _) = await SeedReportsAsync(probe);
		(AdminBrowser moderator, User warden) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);

		PageResponse response = await moderator.Get("/api/v1/lobbies/501/connection_reports");
		Assert.Equal(200, response.Status);
		JsonElement[] rows = Json(response).GetProperty("reports").EnumerateArray().ToArray();

		// connections lobby lines up a pair attempt by attempt, the lower user id first
		List<ConnectionReport> cli;
		await using (AppDbContext db = probe.Open())
		{
			cli = Database.ConnectionReports.InPairs(await Database.ConnectionReports.GetByLobby(db, 501)).SelectMany(p => p.Reports).ToList();
		}
		Assert.Equal(cli.Select(r => (r.LobbyID, r.ReporterID, r.TargetUserID, r.Attempt)), rows.Select(Key));
		long low = Math.Min(alpha.ID, bravo.ID), high = Math.Max(alpha.ID, bravo.ID);
		Assert.Equal(new[] { (501L, low, high, 1), (501L, high, low, 1), (501L, alpha.ID, bravo.ID, 2) }, rows.Select(Key));

		AuditLogEntry entry = Assert.Single(await AuditOf(probe, ModerationActions.ConnectionLookup));
		Assert.Equal((AuditActorKinds.User, (long?)warden.ID, AuditTargetKinds.Lobby, (long?)501), (entry.ActorKind, entry.ActorID, entry.TargetKind, entry.TargetID));
		Assert.Equal("lobby", JsonDocument.Parse(entry.Details!).RootElement.GetProperty("by").GetString());

		Assert.Empty(Json(await moderator.Get("/api/v1/lobbies/777/connection_reports")).GetProperty("reports").EnumerateArray());
		Assert.Equal(400, (await moderator.Get("/api/v1/lobbies/0/connection_reports")).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ConnectionReportsAreForStaffOnly(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(User alpha, _, _) = await SeedReportsAsync(probe);
		var player = new AdminBrowser(probe);
		await player.SignUp("Plain", "plain@example.org");

		foreach (string path in new[] { $"/api/v1/players/{alpha.ID}/connection_reports", "/api/v1/lobbies/501/connection_reports" })
		{
			PageResponse refused = await player.Get(path);
			Assert.Equal(403, refused.Status);
			Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
			Assert.Equal(401, (await new AdminBrowser(probe).Get(path)).Status);
		}
		Assert.Empty(await AuditOf(probe, ModerationActions.ConnectionLookup));
	}

	// The CLI's lines of a report stay as they were
	[Fact]
	public void TheCliDescribesAConnectionReportAsBefore()
	{
		ConnectionReport report = Report(501, 11, 12, 2, ConnectionReportOutcomes.ConnectedRelay, new DateTime(2026, 11, 18, 10, 0, 3, DateTimeKind.Utc));
		Assert.Equal("lobby 501  user 11 -> user 12  attempt 2  connected_relay  relay  ipv4  updated 2026-11-18 10:00:03 UTC  events [connecting@0ms, connected_relay@4200ms]", ModerationCli.DescribeConnection(report));
	}
}
