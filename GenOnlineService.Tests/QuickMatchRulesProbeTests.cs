using Database;
using GenOnlineService.Api;
using GenOnlineService.Realtime;
using GenOnlineService.Rest;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests;

// A refused quick match request is kept for staff review once per player and lobby (GENONLINE_PROBE_POSTGRES / GENONLINE_PROBE_MARIADB)
public class QuickMatchRulesProbeTests
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

	private static readonly DateTime s_now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

	private static async Task<User> AddUser(ProbeDatabase probe, string name, int tag)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, State = UserStates.Active, CreatedAt = s_now, UpdatedAt = s_now };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}

	private static AnticheatQuickMatchReport Report(long userID, long lobbyID, string method, string sides, DateTime at) => new()
	{
		UserId = userID, MatchId = 0, LobbyId = lobbyID, PlaylistId = 3, Method = method, Sides = sides, CreatedAt = at
	};

	private static async Task<bool> Flag(ProbeDatabase probe, AnticheatQuickMatchReport report)
	{
		await using AppDbContext db = probe.Open();
		return await Database.AntiCheat.FlagAccountForReview_QuickMatchRules(db, report);
	}

	private static ModerationActor Staff(long userID) => new() { Kind = AuditActorKinds.User, UserID = userID, Name = $"user:{userID}", Source = ModerationSources.Api };

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ARefusalIsKeptOncePerPlayerAndLobbyAndListedWithItsRequest(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User cheater = await AddUser(probe, "Cheater", 1);
		User other = await AddUser(probe, "Other", 2);
		var factory = new Factory(probe);

		Assert.True(await Flag(probe, Report(cheater.ID, 991, "UpdateMySlot", "2", s_now)));

		// a repeat in the same lobby is dropped without an error, another lobby or another player is a new row
		Assert.True(await Flag(probe, Report(cheater.ID, 991, "UpdateSlots", "1,3", s_now.AddSeconds(5))));
		Assert.True(await Flag(probe, Report(cheater.ID, 992, "UpdateSlots", "-1,4", s_now.AddMinutes(1))));
		Assert.True(await Flag(probe, Report(other.ID, 991, "UpdateMySlot", "0", s_now.AddMinutes(2))));

		(int total, AntiCheatReportsResponse? list, Failure? failure) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.QuickMatchRules, new AntiCheatListArgs(false, cheater.ID), 0, 10);
		Assert.Null(failure);
		Assert.Equal(2, total);
		AntiCheatReport newest = list!.Reports[0];
		AntiCheatReport first = list.Reports[1];
		Assert.Equal("quickmatch_rules", first.Kind);
		Assert.Equal(cheater.ID, first.Player.Id);
		Assert.Null(first.MatchId);
		Assert.Equal(991L, first.LobbyId);
		Assert.Equal(3, first.PlaylistId);
		Assert.Equal("UpdateMySlot", first.Method);
		Assert.Equal([2], first.Sides!);
		Assert.Equal(s_now, first.ReportedAt!.Value.ToUniversalTime());
		Assert.Equal(992L, newest.LobbyId);
		Assert.Equal([-1, 4], newest.Sides!);

		(total, _, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.QuickMatchRules, new AntiCheatListArgs(false, null), 0, 10);
		Assert.Equal(3, total);

		// the request fields go on the wire for this list only
		JsonNode json = JsonSerializer.SerializeToNode(first, ApiResults.Json)!;
		Assert.Equal(991, (long)json["lobby_id"]!);
		Assert.Equal("UpdateMySlot", (string?)json["method"]);
		Assert.Equal(2, (int)json["sides"]![0]!);
		Assert.NotNull(json["reported_at"]);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffMarkARefusalReviewedAndItLeavesTheUnreviewedList(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User cheater = await AddUser(probe, "Cheater", 1);
		User admin = await AddUser(probe, "Admin", 2);
		var factory = new Factory(probe);
		Assert.True(await Flag(probe, Report(cheater.ID, 991, "UpdateMySlot", "2", s_now)));

		(_, AntiCheatReportsResponse? list, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.QuickMatchRules, new AntiCheatListArgs(false, null), 0, 10);
		long reportID = list!.Reports.Single().Id;

		Assert.True((await AntiCheatActions.MarkReviewedAsync(factory, Staff(admin.ID), AntiCheatReviewKinds.QuickMatchRules, reportID, new AntiCheatReviewRequest { Reviewed = true })).Ok);

		(int total, _, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.QuickMatchRules, new AntiCheatListArgs(false, null), 0, 10);
		Assert.Equal(0, total);
		(total, list, _) = await AntiCheatActions.ListAsync(factory, AntiCheatReviewKinds.QuickMatchRules, new AntiCheatListArgs(true, null), 0, 10);
		Assert.Equal(1, total);
		Assert.NotNull(list!.Reports[0].ReviewedAt);
		Assert.Equal(admin.ID, list.Reports[0].ReviewedBy);

		// a quickmatch_rules id is not a report of another list
		Assert.Equal(ErrorCodes.NotFound, (await AntiCheatActions.MarkReviewedAsync(factory, Staff(admin.ID), AntiCheatReviewKinds.Probes, reportID + 1000, new AntiCheatReviewRequest { Reviewed = true })).Code);
	}
}
