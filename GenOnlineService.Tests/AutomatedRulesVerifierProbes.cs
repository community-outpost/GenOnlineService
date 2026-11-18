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

using GenOnlineService.Rules;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;
using static GenOnlineService.Tests.AutomatedRulesTests;

namespace GenOnlineService.Tests;

// The regression probes of the independent review: unknown document properties, concurrent sweep starts and
// concurrent bans of one player
[Collection(AdminAreaCollection.Name)]
public class AutomatedRulesVerifierProbes
{
	// A ban node with a misspelt length must not be stored as a permanent ban
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnUnknownNodePropertyIsRefusedNotIgnored(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			object document = new
			{
				nodes = new object[]
				{
					new { id = "t", kind = "trigger", trigger = "sign_in" },
					new { id = "c", kind = "condition", query = Condition("account.name", "starts_with", "Target") },
					new { id = "a", kind = "action", action = "ban", duration = 86400, reason = "one day" }
				},
				edges = new object[] { new { from = "t", to = "c" }, new { from = "c", to = "a", output = "true" } }
			};
			PageResponse created = await admin.SendJson("POST", RulesPath, new { name = "typo", document }, csrf);
			Assert.Equal(400, created.Status);
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	// Two starts of one sweep at the same time run one sweep
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TwoConcurrentStartsRunOneSweep(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		var hold = new TaskCompletionSource();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			RuleRunner.Configure(Environment(probe));
			RuleSweeps.Serving = () => true;
			RuleSweeps.BeforeBatch = _ => hold.Task;
			var statuses = new List<int>();
			for (int attempt = 0; attempt < 5; attempt++)
			{
				PageResponse created = await admin.SendJson("POST", RulesPath, new { name = "held" + attempt, document = Document("sweep", Condition("account.name", "starts_with", "Target")) }, csrf);
				long id = Json(created).GetProperty("rule").GetProperty("id").GetInt64();
				PageResponse[] both = await Task.WhenAll(admin.SendJson("POST", $"{RulesPath}/{id}/sweep", new { }, csrf), admin.SendJson("POST", $"{RulesPath}/{id}/sweep", new { }, csrf));
				statuses.AddRange(both.Select(r => r.Status));
			}
			Assert.Equal(5, statuses.Count(s => s == 201));
			Assert.Equal(5, (await AuditOf(probe, RuleAudit.SweepStart)).Count);
		}
		finally
		{
			hold.TrySetResult();
			RuleSweeps.BeforeBatch = null;
			RuleSweeps.Serving = () => Failover.RealtimeFailover.Serving;
			RuleRunner.Configure(null);
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	// Two copies handling events of one player at once ban the player once
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ConcurrentEventsOfOnePlayerBanOnce(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			Assert.Equal(201, (await admin.SendJson("POST", RulesPath, new { name = "ban", mode = "enforce", enabled = true, document = Document("sign_in", Condition("account.name", "starts_with", "Target"), "ban", null, "evasion") }, csrf)).Status);
			Assert.Equal(201, (await admin.SendJson("POST", RulesPath, new { name = "ban2", mode = "enforce", enabled = true, document = Document("game_session", Condition("account.name", "starts_with", "Target"), "ban", null, "evasion") }, csrf)).Status);
			int doubled = 0;
			for (int i = 0; i < 10; i++)
			{
				long user;
				await using (AppDbContext db = probe.Open())
				{
					user = (await Database.Users.Create(db, "Target" + i)).ID;
				}
				RuleEnvironment env = Environment(probe);
				await Task.WhenAll(
					Task.Run(() => RuleRunner.ProcessAsync(env, RuleEvent.Of(RuleTriggers.SignIn, user), RuleEngine.Current.For(RuleTriggers.SignIn))),
					Task.Run(() => RuleRunner.ProcessAsync(env, RuleEvent.Of(RuleTriggers.GameSession, user), RuleEngine.Current.For(RuleTriggers.GameSession))));
				await using (AppDbContext db = probe.Open())
				{
					if (await db.Sanctions.CountAsync(s => s.UserID == user && s.Type == SanctionTypes.Ban) > 1)
					{
						doubled++;
					}
				}
			}
			Assert.Equal(0, doubled);
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	// Two events of one player one after the other ban the player once
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SequentialEventsOfOnePlayerBanOnce(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			Assert.Equal(201, (await admin.SendJson("POST", RulesPath, new { name = "ban", mode = "enforce", enabled = true, document = Document("sign_in", Condition("account.name", "starts_with", "Target"), "ban", null, "evasion") }, csrf)).Status);
			Assert.Equal(201, (await admin.SendJson("POST", RulesPath, new { name = "ban2", mode = "enforce", enabled = true, document = Document("game_session", Condition("account.name", "starts_with", "Target"), "ban", null, "evasion") }, csrf)).Status);
			int doubled = 0;
			for (int i = 0; i < 10; i++)
			{
				long user;
				await using (AppDbContext db = probe.Open())
				{
					user = (await Database.Users.Create(db, "Target" + i)).ID;
				}
				RuleEnvironment env = Environment(probe);
				await RuleRunner.ProcessAsync(env, RuleEvent.Of(RuleTriggers.SignIn, user), RuleEngine.Current.For(RuleTriggers.SignIn));
				await RuleRunner.ProcessAsync(env, RuleEvent.Of(RuleTriggers.GameSession, user), RuleEngine.Current.For(RuleTriggers.GameSession));
				await using (AppDbContext db = probe.Open())
				{
					if (await db.Sanctions.CountAsync(s => s.UserID == user && s.Type == SanctionTypes.Ban) > 1)
					{
						doubled++;
					}
				}
			}
			Assert.Equal(0, doubled);
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	// A number beyond double answers as a problem, not a server error
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnOverflowingNumberIsAProblem(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		JsonElement huge = JsonDocument.Parse("[0, 1e400]").RootElement;
		PageResponse validated = await admin.SendJson("POST", RulesPath + "/validate", new { document = Document("sign_in", Condition("account.age_days", "between", huge)) }, await admin.Csrf());
		Assert.Equal(200, validated.Status);
	}

	// Nobody without rules.manage or anticheat.review reaches the rule routes
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AModeratorReachesNoRuleRoute(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		Dictionary<string, string> csrf = await moderator.Csrf();
		Assert.Equal(403, (await moderator.Get(RulesPath)).Status);
		Assert.Equal(403, (await moderator.Get(RulesPath + "/fields")).Status);
		Assert.Equal(403, (await moderator.Get(RulesPath + "/matches")).Status);
		Assert.Equal(403, (await moderator.SendJson("PATCH", RulesPath + "/matches/1", new { review = "reviewed" }, csrf)).Status);
		Assert.Equal(403, (await moderator.SendJson("POST", RulesPath + "/1/sweep", new { }, csrf)).Status);
		Assert.Equal(403, (await moderator.SendJson("POST", RulesPath, new { name = "x", document = Document("sign_in", Condition("account.name", "equals", "x")) }, csrf)).Status);
	}
}
