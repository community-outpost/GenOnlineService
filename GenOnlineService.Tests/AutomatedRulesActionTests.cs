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

using GenOnlineService.Investigation;
using GenOnlineService.Rules;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;
using Xunit;
using static GenOnlineService.Tests.AutomatedRulesTests;

namespace GenOnlineService.Tests;

// The automated rules at work for S4, S5 and S7: the modes, the automation actor on every sanction and audit row, staff never
// sanctioned, no second ban or mute, the duplicate window, the circuit breaker, the hooks that queue an event without
// waiting or failing, and the review queue with the staff Flagged list.
[Collection(AdminAreaCollection.Name)]
public class AutomatedRulesActionTests
{
	private static async Task<long> CreateRuleAsync(AdminBrowser admin, Dictionary<string, string> csrf, string name, string mode, object document)
	{
		PageResponse created = await admin.SendJson("POST", RulesPath, new { name, mode, enabled = true, document }, csrf);
		Assert.Equal(201, created.Status);
		return Json(created).GetProperty("rule").GetProperty("id").GetInt64();
	}

	// Two actions on the true output of one condition
	private static object TwoActions(string trigger, object query, object first, object second) => new
	{
		nodes = new object[]
		{
			new { id = "t", kind = "trigger", trigger },
			new { id = "c", kind = "condition", query },
			first,
			second
		},
		edges = new object[]
		{
			new { from = "t", to = "c" },
			new { from = "c", to = "a1", output = "true" },
			new { from = "c", to = "a2", output = "true" }
		}
	};

	private static async Task<long> PlayerAsync(ProbeDatabase probe, string name)
	{
		await using AppDbContext db = probe.Open();
		return (await Database.Users.Create(db, name)).ID;
	}

	private static async Task<List<AutomatedRuleMatch>> MatchesOf(ProbeDatabase probe, long userID)
	{
		await using AppDbContext db = probe.Open();
		return await db.AutomatedRuleMatches.AsNoTracking().Where(m => m.UserID == userID).OrderBy(m => m.ID).ToListAsync();
	}

	private static async Task<List<Sanction>> SanctionsOf(ProbeDatabase probe, long userID)
	{
		await using AppDbContext db = probe.Open();
		return await db.Sanctions.AsNoTracking().Where(s => s.UserID == userID).OrderBy(s => s.ID).ToListAsync();
	}

	private static Task<List<AutomatedRuleMatch>> SignInAsync(ProbeDatabase probe, long userID) =>
		RuleRunner.ProcessAsync(Environment(probe), RuleEvent.Of(RuleTriggers.SignIn, userID), RuleEngine.Current.For(RuleTriggers.SignIn));

	private static readonly object s_target = Condition("account.name", "starts_with", "Target");

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task WatchRecordsFlagQueuesAndEnforceSanctionsAsTheRule(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			long watch = await CreateRuleAsync(admin, csrf, "watch", RuleModes.Watch, Document("sign_in", s_target, "ban", null, "watched"));
			long flag = await CreateRuleAsync(admin, csrf, "flag", RuleModes.Flag, Document("sign_in", s_target, "ban", null, "flagged"));
			long enforce = await CreateRuleAsync(admin, csrf, "enforce", RuleModes.Enforce, Document("sign_in", s_target, "ban", null, "Ban evasion"));
			long target = await PlayerAsync(probe, "TargetOne");
			long bystander = await PlayerAsync(probe, "Bystander");

			Assert.Empty(await SignInAsync(probe, bystander));
			Assert.Equal(3, (await SignInAsync(probe, target)).Count);

			Dictionary<long, AutomatedRuleMatch> matches = (await MatchesOf(probe, target)).ToDictionary(m => m.RuleID);
			Assert.Equal((RuleOutcomes.Watched, RuleReviewStates.None, (string?)null), (matches[watch].Outcome, matches[watch].ReviewState, matches[watch].Actions));
			Assert.Equal((RuleOutcomes.Flagged, RuleReviewStates.Pending, (string?)null), (matches[flag].Outcome, matches[flag].ReviewState, matches[flag].Actions));
			Assert.Equal((RuleOutcomes.Enforced, RuleReviewStates.None, "ban", 1, RuleTriggers.SignIn), (matches[enforce].Outcome, matches[enforce].ReviewState, matches[enforce].Actions, matches[enforce].RuleVersion, matches[enforce].Trigger));
			Assert.Equal("TargetOne", JsonDocument.Parse(matches[enforce].Facts).RootElement.GetProperty("account.name")[0].GetString());

			// one ban, by the rule and its version, with the reason of the action node
			Sanction ban = Assert.Single(await SanctionsOf(probe, target));
			Assert.Equal((SanctionTypes.Ban, AuditActorKinds.Automation, (long?)null, "Ban evasion", (DateTime?)null), (ban.Type, ban.CreatedByKind, ban.CreatedByID, ban.Reason, ban.ExpiresAt));
			AuditLogEntry audit = Assert.Single(await AuditOf(probe, ModerationActions.Ban));
			Assert.Equal((AuditActorKinds.Automation, $"rule:{enforce}@v1", ModerationSources.AutomatedRules, (long?)target, "Ban evasion"), (audit.ActorKind, audit.Actor, audit.Source, audit.TargetID, audit.Reason));
			Assert.Empty(await SanctionsOf(probe, bystander));
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffAreNeverSanctionedAndNothingIsSanctionedTwice(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			object document = TwoActions("sign_in", s_target,
				new { id = "a1", kind = "action", action = "ban", reason = "Ban evasion" },
				new { id = "a2", kind = "action", action = "mute", duration_s = 600, reason = "Ban evasion" });
			await CreateRuleAsync(admin, csrf, "evasion", RuleModes.Enforce, document);

			long moderator = await PlayerAsync(probe, "TargetModerator");
			await using (AppDbContext db = probe.Open())
			{
				db.UserRoles.Add(new UserRole { UserID = moderator, Role = StaffRoles.Moderator, Source = AuthoritySources.Local, GrantedAt = DateTime.UtcNow });
				await db.SaveChangesAsync();
			}
			Assert.Equal(RuleOutcomes.StaffExempt, Assert.Single(await SignInAsync(probe, moderator)).Outcome);
			Assert.Empty(await SanctionsOf(probe, moderator));

			// a player already banned and muted keeps those sanctions and gets no second one
			long banned = await PlayerAsync(probe, "TargetBanned");
			await using (AppDbContext db = probe.Open())
			{
				var staff = new Database.ModerationActor { Kind = AuditActorKinds.Cli, Name = "cli", Source = ModerationSources.Cli };
				await Database.Moderation.AddSanction(db, banned, SanctionTypes.Ban, "earlier", null, staff);
				await Database.Moderation.AddSanction(db, banned, SanctionTypes.Mute, "earlier", DateTime.UtcNow.AddDays(1), staff);
			}
			AutomatedRuleMatch nothing = Assert.Single(await SignInAsync(probe, banned));
			Assert.Equal((RuleOutcomes.NothingToDo, (string?)null), (nothing.Outcome, nothing.Actions));
			Assert.Equal(2, (await SanctionsOf(probe, banned)).Count);

			// both actions on a fresh player, then the duplicate window keeps the next sign-in from matching again
			long fresh = await PlayerAsync(probe, "TargetFresh");
			Assert.Equal("ban,mute", Assert.Single(await SignInAsync(probe, fresh)).Actions);
			Assert.Equal([SanctionTypes.Ban, SanctionTypes.Mute], (await SanctionsOf(probe, fresh)).Select(s => s.Type).ToArray());
			Assert.Empty(await SignInAsync(probe, fresh));
			Assert.Single(await MatchesOf(probe, fresh));
			Assert.Equal(2, (await SanctionsOf(probe, fresh)).Count);
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnEnforceRuleThatActsTooOftenFallsBackToFlag(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		int limit = RuleRunner.BreakerLimitPerHour;
		try
		{
			RuleRunner.BreakerLimitPerHour = 2;
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			long id = await CreateRuleAsync(admin, await admin.Csrf(), "mute all", RuleModes.Enforce, Document("sign_in", s_target, "mute", 600, "Spam wave"));

			for (int i = 0; i < 2; i++)
			{
				Assert.Equal(RuleOutcomes.Enforced, Assert.Single(await SignInAsync(probe, await PlayerAsync(probe, "TargetMuted" + i))).Outcome);
			}
			long third = await PlayerAsync(probe, "TargetThird");
			AutomatedRuleMatch tripped = Assert.Single(await SignInAsync(probe, third));
			Assert.Equal((RuleModes.Flag, RuleOutcomes.Flagged, RuleReviewStates.Pending), (tripped.Mode, tripped.Outcome, tripped.ReviewState));
			Assert.Empty(await SanctionsOf(probe, third));

			await using (AppDbContext db = probe.Open())
			{
				Assert.Equal(RuleModes.Flag, (await db.AutomatedRules.SingleAsync(r => r.ID == id)).Mode);
			}
			AuditLogEntry breaker = Assert.Single(await AuditOf(probe, RuleAudit.Breaker));
			Assert.Equal((AuditActorKinds.Automation, $"rule:{id}@v1", (long?)id), (breaker.ActorKind, breaker.Actor, breaker.TargetID));
			Assert.Equal(RuleModes.Flag, Assert.Single(RuleEngine.Current.Rules).Mode);

			// the list shows the trip and the last match until staff change the mode again
			JsonElement listed = Assert.Single(Json(await admin.Get(RulesPath)).GetProperty("rules").EnumerateArray());
			Assert.Equal(tripped.MatchedAt, listed.GetProperty("last_match_at").GetDateTime(), TimeSpan.FromSeconds(1));
			Assert.Equal(JsonValueKind.String, listed.GetProperty("breaker_tripped_at").ValueKind);
			JsonElement back = Json(await admin.Patch($"{RulesPath}/{id}", new { mode = RuleModes.Enforce }, await admin.Csrf()));
			Assert.False(back.TryGetProperty("breaker_tripped_at", out JsonElement cleared) && cleared.ValueKind != JsonValueKind.Null);
			Assert.Equal(JsonValueKind.String, back.GetProperty("last_match_at").ValueKind);
		}
		finally
		{
			RuleRunner.BreakerLimitPerHour = limit;
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	// A GeoIP lookup that always fails
	private sealed class BrokenAddresses : IAddressDirectory
	{
		public AddressFacts? Lookup(string ipAddress) => throw new InvalidOperationException("the GeoIP database is gone");
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AFailingRuleNeverFailsTheSignInAndTheOtherRulesStillRun(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			long broken = await CreateRuleAsync(admin, csrf, "country", RuleModes.Flag, Document("sign_in", Condition("network.country", "equals", "NL"), "flag"));
			long names = await CreateRuleAsync(admin, csrf, "names", RuleModes.Flag, Document("sign_in", Condition("account.name", "equals", "Newcomer"), "flag"));
			RuleRunner.Configure(Environment(probe, new BrokenAddresses()));

			// the sign-up signs the player in and queues the event, and the page answers as always
			var player = new AdminBrowser(probe) { RemoteAddress = IPAddress.Parse("203.0.113.9") };
			await player.SignUp("Newcomer", "newcomer@example.org");
			await RuleRunner.DrainAsync(TimeSpan.FromSeconds(10));

			User user = (await PasswordPagesTests.UserByEmail(probe, "newcomer@example.org"))!;
			AutomatedRuleMatch match = Assert.Single(await MatchesOf(probe, user.ID));
			Assert.Equal((names, RuleTriggers.SignIn), (match.RuleID, match.Trigger));
			Assert.NotEqual(broken, match.RuleID);
		}
		finally
		{
			RuleRunner.Configure(null);
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	// The anti-cheat writers queue their event after the row is stored
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ANewAntiCheatRowRunsTheRulesOfItsTrigger(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			await CreateRuleAsync(admin, await admin.Csrf(), "fresh accounts", RuleModes.Flag,
				Document("anticheat_report", Group("all", Condition("event.anticheat_kind", "equals", "new_accounts"), Condition("anticheat.new_accounts", "greater_than", 0))));
			RuleRunner.Configure(Environment(probe));
			long player = await PlayerAsync(probe, "Rookie");
			await using (AppDbContext db = probe.Open())
			{
				await Database.AntiCheat.FlagAccountForReview_NewAccount_FirstMatches(db, player, 77);
			}
			await RuleRunner.DrainAsync(TimeSpan.FromSeconds(10));
			AutomatedRuleMatch match = Assert.Single(await MatchesOf(probe, player));
			JsonElement facts = JsonDocument.Parse(match.Facts).RootElement;
			Assert.Equal(("new_accounts", 1), (facts.GetProperty("event.anticheat_kind")[0].GetString(), facts.GetProperty("anticheat.new_accounts")[0].GetInt32()));
		}
		finally
		{
			RuleRunner.Configure(null);
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffReviewTheFlaggedMatchesAndTheFlaggedListShowsThem(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
			Dictionary<string, string> csrf = await admin.Csrf();
			long rule = await CreateRuleAsync(admin, csrf, "suspects", RuleModes.Flag, Document("sign_in", s_target, "flag"));
			long target = await PlayerAsync(probe, "TargetSuspect");
			long id = Assert.Single(await SignInAsync(probe, target)).ID;

			Assert.Equal(403, (await moderator.Get(RulesPath + "/matches")).Status);
			PageResponse listed = await admin.Get(RulesPath + "/matches");
			Assert.Equal("1", listed.Headers!["X-Total"].ToString());
			JsonElement row = Assert.Single(Json(listed).GetProperty("matches").EnumerateArray());
			Assert.Equal((id, rule, "suspects", target, "pending", "flagged"), (row.GetProperty("id").GetInt64(), row.GetProperty("rule_id").GetInt64(), row.GetProperty("rule_name").GetString(), row.GetProperty("player").GetProperty("id").GetInt64(), row.GetProperty("review_state").GetString(), row.GetProperty("outcome").GetString()));
			Assert.Equal("TargetSuspect", row.GetProperty("facts").GetProperty("account.name")[0].GetString());
			Assert.Equal(400, (await admin.Get(RulesPath + "/matches?review=maybe")).Status);

			// the staff Flagged list holds the player for a reviewer, as an unreviewed anti-cheat row does
			Assert.Contains(target, Json(await admin.Get("/api/v1/players/search?filter=flagged")).GetProperty("players").EnumerateArray().Select(p => p.GetProperty("id").GetInt64()));

			Assert.Equal(400, (await admin.Patch($"{RulesPath}/matches/{id}", new { review = "pending" }, csrf)).Status);
			Assert.Equal(404, (await admin.Patch($"{RulesPath}/matches/999999", new { review = "reviewed" }, csrf)).Status);
			PageResponse reviewed = await admin.Patch($"{RulesPath}/matches/{id}", new { review = "reviewed" }, csrf);
			Assert.Equal((200, "reviewed", chief.ID), (reviewed.Status, Json(reviewed).GetProperty("review_state").GetString(), Json(reviewed).GetProperty("reviewed_by").GetInt64()));
			AuditLogEntry entry = Assert.Single(await AuditOf(probe, RuleAudit.MatchReview));
			Assert.Equal((RuleAudit.MatchTargetKind, (long?)id), (entry.TargetKind, entry.TargetID));

			// a match reviewed before answers as it is and writes nothing
			Assert.Equal("reviewed", Json(await admin.Patch($"{RulesPath}/matches/{id}", new { review = "dismissed" }, csrf)).GetProperty("review_state").GetString());
			Assert.Single(await AuditOf(probe, RuleAudit.MatchReview));
			Assert.Empty(Json(await admin.Get(RulesPath + "/matches")).GetProperty("matches").EnumerateArray());
			Assert.Single(Json(await admin.Get(RulesPath + "/matches?review=all&user_id=" + target)).GetProperty("matches").EnumerateArray());
			Assert.DoesNotContain(target, Json(await admin.Get("/api/v1/players/search?filter=flagged")).GetProperty("players").EnumerateArray().Select(p => p.GetProperty("id").GetInt64()));
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}
}
