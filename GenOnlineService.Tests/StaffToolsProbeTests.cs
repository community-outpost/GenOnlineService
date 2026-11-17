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
using GenOnlineService.Realtime;
using GenOnlineService.Rest;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GenOnlineService.Tests;

// The staff tool queries against a real database (GENONLINE_PROBE_POSTGRES / GENONLINE_PROBE_MARIADB)
public class StaffToolsProbeTests
{
	private sealed class Factory : IDbContextFactory<AppDbContext>
	{
		private readonly ProbeDatabase m_probe;

		public Factory(ProbeDatabase probe)
		{
			m_probe = probe;
		}

		public AppDbContext CreateDbContext() => m_probe.Open();
	}

	private static readonly DateTime s_now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

	private static async Task<User> AddUser(ProbeDatabase probe, string name, int tag, string state = UserStates.Active)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, State = state, CreatedAt = s_now, UpdatedAt = s_now };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}

	private static async Task AddDevice(ProbeDatabase probe, long userID, string kind, byte value, DateTime seen)
	{
		await using AppDbContext db = probe.Open();
		db.Devices.Add(new Device { UserID = userID, Kind = kind, ValueHmac = Enumerable.Repeat(value, Device.ValueHmacLength).ToArray(), FirstSeenAt = seen.AddDays(-1), LastSeenAt = seen, LastIp = "192.0.2.1" });
		await db.SaveChangesAsync();
	}

	private static async Task AddEvent(ProbeDatabase probe, long userID, string ip, string? country, DateTime at)
	{
		await using AppDbContext db = probe.Open();
		db.SessionEvents.Add(new SessionEvent { UserID = userID, Kind = SessionEventKinds.Start, Result = SessionEventResults.Ok, Client = "genonline", Ip = ip, Country = country, CreatedAt = at });
		await db.SaveChangesAsync();
	}

	private static ModerationActor Staff(long userID) => new() { Kind = AuditActorKinds.User, UserID = userID, Name = $"user:{userID}", Source = ModerationSources.Api };

	// The staff search, through the player list that replaced Users.StaffSearch (T17f)
	private static async Task<(int Total, List<StaffPlayerRow> Page)> Search(AppDbContext db, string term, int skip, int take)
	{
		StaffPlayerList list = await StaffPlayerLists.ListAsync(db, new StaffPlayerQuery { Term = term }, DateTime.UtcNow, skip, take);
		return (list.Total!.Value, list.Players.ToList());
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffSearchMatchesAnywhereInTheNameAndTheExactId(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User first = await AddUser(probe, "RedGeneral", 1);
		User second = await AddUser(probe, "general_blue", 2, UserStates.Blocked);
		User third = await AddUser(probe, "Tank%Rush", 3);
		await using AppDbContext db = probe.Open();

		(int total, List<StaffPlayerRow> page) = await Search(db, "GENERAL", 0, 10);
		Assert.Equal(2, total);
		Assert.Equal([second.ID, first.ID], page.Select(p => p.ID));
		Assert.Equal(UserStates.Blocked, page[0].State);

		// paged in the database: the second page of one holds the second name
		(total, page) = await Search(db, "general", 1, 1);
		Assert.Equal(2, total);
		Assert.Equal([first.ID], page.Select(p => p.ID));

		// a wildcard character in the term is a plain character
		(total, page) = await Search(db, "k%r", 0, 10);
		Assert.Equal([third.ID], page.Select(p => p.ID));
		(total, _) = await Search(db, "%", 0, 10);
		Assert.Equal(1, total);

		(total, page) = await Search(db, third.ID.ToString(), 0, 10);
		Assert.Equal(1, total);
		Assert.Equal(third.ID, page[0].ID);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ReviewListsDefaultToUnreviewedAndMarkingKeepsTheRow(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User player = await AddUser(probe, "Suspect", 1);
		User other = await AddUser(probe, "Other", 2);
		User admin = await AddUser(probe, "Admin", 3);
		await using (AppDbContext seed = probe.Open())
		{
			seed.AnticheatNewAccountGames.AddRange(Enumerable.Range(1, 5).Select(i => new AnticheatNewAccountGame { UserId = i == 5 ? other.ID : player.ID, MatchId = 100 + i }));
			seed.AnticheatModuleReports.Add(new AnticheatModuleReport { UserId = player.ID, MatchId = 7, ModuleName = "inject.dll", ModulePath = "C:\\Temp\\inject.dll", ModuleSize = 4096 });
			seed.AnticheatProbeReports.Add(new AnticheatProbeReport { UserId = player.ID, MatchId = 7, Reason = "Malformed probe response (Type 2)" });
			seed.AnticheatProbeReports.Add(new AnticheatProbeReport { UserId = player.ID, MatchId = 0, Reason = "Logged in with potentially spoofed HWID 0" });
			await seed.SaveChangesAsync();
		}
		Factory factory = new(probe);

		(int total, AntiCheatReportsResponse? list, Failure? failure) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.NewAccounts, new AntiCheatListArgs(false, null), 0, 2);
		Assert.Null(failure);
		Assert.Equal(5, total);
		Assert.Equal([105L, 104L], list!.Reports.Select(r => r.MatchId!.Value));
		Assert.Equal("Other", list.Reports[0].Player.Name);
		Assert.Equal("0002", list.Reports[0].Player.Tag);

		(total, list, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.NewAccounts, new AntiCheatListArgs(false, player.ID), 2, 2);
		Assert.Equal(4, total);
		Assert.Equal([102L, 101L], list!.Reports.Select(r => r.MatchId!.Value));

		// the sign-in device reports and the match probes are two lists over one table
		(total, list, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.Probes, new AntiCheatListArgs(false, null), 0, 10);
		Assert.Equal("Malformed probe response (Type 2)", Assert.Single(list!.Reports).Reason);
		(total, list, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.Devices, new AntiCheatListArgs(false, null), 0, 10);
		AntiCheatReport device = Assert.Single(list!.Reports);
		Assert.Null(device.MatchId);

		(total, list, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.Modules, new AntiCheatListArgs(false, null), 0, 10);
		AntiCheatReport module = Assert.Single(list!.Reports);
		Assert.Equal(("inject.dll", "C:\\Temp\\inject.dll", 4096L), (module.ModuleName, module.ModulePath, module.ModuleSize));

		// marking: the row leaves the default list, stays in the table with its reviewer, and is audited once
		Result<Empty> marked = await AntiCheatActions.MarkReviewedAsync(factory, Staff(admin.ID), AntiCheatReviewKinds.Modules, module.Id, new AntiCheatReviewRequest { Reviewed = true });
		Assert.True(marked.Ok, marked.Code);
		Assert.True((await AntiCheatActions.MarkReviewedAsync(factory, Staff(other.ID), AntiCheatReviewKinds.Modules, module.Id, new AntiCheatReviewRequest { Reviewed = true })).Ok);

		(total, _, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.Modules, new AntiCheatListArgs(false, null), 0, 10);
		Assert.Equal(0, total);
		(total, list, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.Modules, new AntiCheatListArgs(true, null), 0, 10);
		Assert.Equal(admin.ID, Assert.Single(list!.Reports).ReviewedBy);
		Assert.NotNull(list.Reports[0].ReviewedAt);
		(total, _, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.Modules, new AntiCheatListArgs(null, null), 0, 10);
		Assert.Equal(1, total);

		await using (AppDbContext db = probe.Open())
		{
			AuditLogEntry entry = await db.AuditLog.SingleAsync(a => a.Action == ModerationActions.AnticheatReview);
			Assert.Equal((AuditTargetKinds.AnticheatReport, (long?)module.Id, (long?)admin.ID), (entry.TargetKind, entry.TargetID, entry.ActorID));
			// jsonb stores the object with its own spacing
			using System.Text.Json.JsonDocument details = System.Text.Json.JsonDocument.Parse(entry.Details!);
			Assert.Equal("modules", details.RootElement.GetProperty("kind").GetString());
			Assert.Equal(player.ID, details.RootElement.GetProperty("user_id").GetInt64());
		}

		// a device report is not reachable through the probes list, and the other way round
		Assert.Equal(ErrorCodes.NotFound, (await AntiCheatActions.MarkReviewedAsync(factory, Staff(admin.ID), AntiCheatReviewKinds.Probes, device.Id, new AntiCheatReviewRequest { Reviewed = true })).Code);
		Assert.Equal(ErrorCodes.NotFound, (await AntiCheatActions.MarkReviewedAsync(factory, Staff(admin.ID), AntiCheatReviewKinds.NewAccounts, 9999, new AntiCheatReviewRequest { Reviewed = true })).Code);
		Assert.Equal(ErrorCodes.InvalidArgument, (await AntiCheatActions.MarkReviewedAsync(factory, Staff(admin.ID), AntiCheatReviewKinds.Devices, device.Id, new AntiCheatReviewRequest())).Code);
		Assert.Equal(ErrorCodes.InvalidArgument, (await AntiCheatActions.MarkReviewedAsync(factory, Staff(admin.ID), "crc", device.Id, new AntiCheatReviewRequest { Reviewed = true })).Code);
		Assert.True((await AntiCheatActions.MarkReviewedAsync(factory, Staff(admin.ID), AntiCheatReviewKinds.Devices, device.Id, new AntiCheatReviewRequest { Reviewed = true })).Ok);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task InspectionListsDevicesAddressesAndDuplicates(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User player = await AddUser(probe, "Player", 1);
		User sameDevice = await AddUser(probe, "SameDevice", 2, UserStates.Blocked);
		User sameAddress = await AddUser(probe, "SameAddress", 3);
		User stranger = await AddUser(probe, "Stranger", 4);

		await AddDevice(probe, player.ID, DeviceKinds.MachineGuid, 0xA1, s_now.AddDays(-2));
		await AddDevice(probe, player.ID, DeviceKinds.Mac, 0xB2, s_now.AddDays(-1));
		await AddDevice(probe, sameDevice.ID, DeviceKinds.Mac, 0xB2, s_now.AddDays(-3));
		// the same bytes under another kind are another device
		await AddDevice(probe, stranger.ID, DeviceKinds.VolumeSerial, 0xA1, s_now);

		await AddEvent(probe, player.ID, "198.51.100.7", "NL", s_now.AddDays(-5));
		await AddEvent(probe, player.ID, "198.51.100.7", "NL", s_now.AddDays(-1));
		await AddEvent(probe, player.ID, "203.0.113.9", "DE", s_now.AddDays(-3));
		await AddEvent(probe, sameAddress.ID, "203.0.113.9", "DE", s_now.AddDays(-4));
		await AddEvent(probe, sameDevice.ID, "198.51.100.7", "NL", s_now.AddDays(-6));
		await AddEvent(probe, stranger.ID, "192.0.2.200", "US", s_now);
		await using AppDbContext db = probe.Open();

		List<InspectedDevice> devices = await StaffInspection.GetDevices(db, player.ID);
		Assert.Equal([DeviceKinds.Mac, DeviceKinds.MachineGuid], devices.Select(d => d.Kind));
		Assert.Equal("b2b2b2b2", devices[0].Fingerprint);
		Assert.Equal(s_now.AddDays(-1), devices[0].LastSeenAt);

		List<InspectedAddress> addresses = await StaffInspection.GetAddresses(db, player.ID);
		Assert.Equal(["198.51.100.7", "203.0.113.9"], addresses.Select(a => a.Ip));
		Assert.Equal(("NL", s_now.AddDays(-5), s_now.AddDays(-1), 2), (addresses[0].Country, addresses[0].FirstSeenAt, addresses[0].LastSeenAt, addresses[0].Events));

		LastSession? last = await StaffInspection.GetLastSession(db, player.ID);
		Assert.Equal(("198.51.100.7", "NL", s_now.AddDays(-1)), (last!.Ip, last.Country, last.At));
		Assert.Null(await StaffInspection.GetLastSession(db, 9999));

		// a shared device ranks first and wins over the shared address of the same account
		List<DuplicateAccount> duplicates = await StaffInspection.GetDuplicates(db, player.ID);
		Assert.Equal([(sameDevice.ID, DuplicateMatches.Device), (sameAddress.ID, DuplicateMatches.Ip)], duplicates.Select(d => (d.UserID, d.Match)));
		Assert.Equal(("SameDevice", 2, UserStates.Blocked, 1), (duplicates[0].Name, duplicates[0].NameTag, duplicates[0].State, duplicates[0].Shared));
		Assert.Empty(await StaffInspection.GetDuplicates(db, stranger.ID));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task InspectionIsAuditedOncePerStaffMemberAndPlayerPerHour(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using AppDbContext db = probe.Open();
		TimeSpan hour = TimeSpan.FromHours(1);

		Assert.True(await Moderation.LogInspection(db, Staff(10), 20, s_now, hour));
		Assert.False(await Moderation.LogInspection(db, Staff(10), 20, s_now.AddMinutes(59), hour));
		Assert.True(await Moderation.LogInspection(db, Staff(10), 21, s_now.AddMinutes(1), hour));
		Assert.True(await Moderation.LogInspection(db, Staff(11), 20, s_now.AddMinutes(2), hour));
		Assert.True(await Moderation.LogInspection(db, Staff(10), 20, s_now.AddMinutes(61), hour));

		List<AuditLogEntry> rows = await db.AuditLog.Where(a => a.Action == ModerationActions.PlayerInspect).OrderBy(a => a.ID).ToListAsync();
		Assert.Equal(4, rows.Count);
		Assert.Equal((AuditTargetKinds.User, (long?)20, (long?)10, "user:10"), (rows[0].TargetKind, rows[0].TargetID, rows[0].ActorID, rows[0].Actor));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ReportQueueFiltersSearchesAndPagesInTheDatabase(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User reporter = await AddUser(probe, "Reporter", 1);
		User reported = await AddUser(probe, "Griefer", 2);
		await using (AppDbContext seed = probe.Open())
		{
			for (int i = 0; i < 120; i++)
			{
				seed.PlayerReports.Add(new PlayerReport
				{
					ReporterID = i == 3 ? reported.ID : reporter.ID,
					ReportedUserID = i == 3 ? reporter.ID : reported.ID,
					Reason = i == 7 ? "Map HACK suspected" : $"Reason {i}",
					Evidence = "{\"messages\":[]}",
					Status = i < 110 ? PlayerReportStatuses.Open : i < 115 ? PlayerReportStatuses.Actioned : PlayerReportStatuses.Dismissed,
					CreatedAt = s_now.AddMinutes(i)
				});
			}
			await seed.SaveChangesAsync();
		}
		await using AppDbContext db = probe.Open();

		// the old list stopped at 100 rows, the page reaches the rows behind them
		(int total, List<PlayerReport> page) = await Chat.GetReportsPage(db, [PlayerReportStatuses.Open], null, false, 100, 20);
		Assert.Equal(110, total);
		Assert.Equal(Enumerable.Range(100, 10).Select(i => $"Reason {i}"), page.Select(r => r.Reason));

		(total, page) = await Chat.GetReportsPage(db, null, null, true, 0, 3);
		Assert.Equal(120, total);
		Assert.Equal(["Reason 119", "Reason 118", "Reason 117"], page.Select(r => r.Reason));

		(total, page) = await Chat.GetReportsPage(db, [PlayerReportStatuses.Actioned, PlayerReportStatuses.Dismissed], null, false, 0, 50);
		Assert.Equal(10, total);
		Assert.All(page, r => Assert.NotEqual(PlayerReportStatuses.Open, r.Status));

		(total, page) = await Chat.GetReportsPage(db, null, "map hack", false, 0, 50);
		Assert.Equal("Map HACK suspected", Assert.Single(page).Reason);

		// a player name matches the reporter and the reported side
		(total, _) = await Chat.GetReportsPage(db, null, "grief", false, 0, 1);
		Assert.Equal(120, total);
		(total, _) = await Chat.GetReportsPage(db, [PlayerReportStatuses.Dismissed], "nobody", false, 0, 1);
		Assert.Equal(0, total);
	}
}
