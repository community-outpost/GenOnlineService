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

// The preview and the manual sweep of S5 and S7: a preview lists who a rule would match now within its limits and writes
// only its audit row, a sweep runs one rule over every player only on the copy that holds the realtime lease, in batches,
// with its progress over REST, and can be cancelled.
[Collection(AdminAreaCollection.Name)]
public class AutomatedRulesSweepTests
{
	private static async Task<long[]> PlayersAsync(ProbeDatabase probe, params string[] names)
	{
		await using AppDbContext db = probe.Open();
		var ids = new List<long>();
		foreach (string name in names)
		{
			ids.Add((await Database.Users.Create(db, name)).ID);
		}
		return ids.ToArray();
	}

	private static readonly object s_target = Condition("account.name", "starts_with", "Target");

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task APreviewListsWhoWouldMatchWithinItsLimitsAndWritesOnlyItsAuditRow(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		(int players, TimeSpan duration, int page) = (RulePreview.MaximumPlayers, RulePreview.MaximumDuration, RulePreview.PageSize);
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
			Dictionary<string, string> csrf = await admin.Csrf();
			long[] targets = await PlayersAsync(probe, "TargetA", "TargetB", "TargetC", "Other");
			RuleRunner.Configure(Environment(probe));
			object document = Document("sign_in", s_target, "ban", null, "would ban");

			Assert.Equal(403, (await moderator.SendJson("POST", RulesPath + "/preview", new { document }, await moderator.Csrf())).Status);

			// every player read: the two staff accounts and four players
			JsonElement all = Json(await admin.SendJson("POST", RulesPath + "/preview", new { document }, csrf));
			Assert.Equal((6, 3, true, false), (all.GetProperty("scanned").GetInt32(), all.GetProperty("matched").GetInt32(), all.GetProperty("complete").GetBoolean(), all.GetProperty("timed_out").GetBoolean()));
			Assert.Equal(targets.Take(3), all.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("player").GetProperty("id").GetInt64()));
			JsonElement first = all.GetProperty("players")[0];
			Assert.Equal(("ban", "TargetA"), (first.GetProperty("actions")[0].GetString(), first.GetProperty("facts").GetProperty("user.name")[0].GetString()));

			// the row limit, the page and the time limit
			RulePreview.MaximumPlayers = 4;
			RulePreview.PageSize = 1;
			JsonElement bounded = Json(await admin.SendJson("POST", RulesPath + "/preview", new { document }, csrf));
			Assert.Equal((4, 2, false, false, 1), (bounded.GetProperty("scanned").GetInt32(), bounded.GetProperty("matched").GetInt32(), bounded.GetProperty("complete").GetBoolean(), bounded.GetProperty("timed_out").GetBoolean(), bounded.GetProperty("players").GetArrayLength()));
			RulePreview.MaximumDuration = TimeSpan.Zero;
			JsonElement late = Json(await admin.SendJson("POST", RulesPath + "/preview", new { document }, csrf));
			Assert.Equal((0, false, true), (late.GetProperty("scanned").GetInt32(), late.GetProperty("complete").GetBoolean(), late.GetProperty("timed_out").GetBoolean()));
			(RulePreview.MaximumPlayers, RulePreview.MaximumDuration, RulePreview.PageSize) = (players, duration, page);

			// a saved rule by its id, a bad document with its problems, an unknown rule
			PageResponse created = await admin.SendJson("POST", RulesPath, new { name = "r", mode = "enforce", enabled = true, document }, csrf);
			long id = Json(created).GetProperty("rule").GetProperty("id").GetInt64();
			Assert.Equal(3, Json(await admin.SendJson("POST", RulesPath + "/preview", new { rule_id = id }, csrf)).GetProperty("matched").GetInt32());
			PageResponse bad = await admin.SendJson("POST", RulesPath + "/preview", new { document = Document("sign_in", Condition("nope", "equals", 1)) }, csrf);
			Assert.Equal((400, "document"), (bad.Status, Json(bad).GetProperty("params").GetProperty("field").GetString()));
			Assert.Equal(404, (await admin.SendJson("POST", RulesPath + "/preview", new { rule_id = 999999 }, csrf)).Status);

			// nothing but the audit rows: no match, no sanction
			await using (AppDbContext db = probe.Open())
			{
				Assert.False(await db.AutomatedRuleMatches.AnyAsync());
				Assert.False(await db.Sanctions.AnyAsync());
			}
			List<AuditLogEntry> previews = await AuditOf(probe, RuleAudit.Preview);
			Assert.Equal(4, previews.Count);
			Assert.Equal(id, previews[3].TargetID);
		}
		finally
		{
			(RulePreview.MaximumPlayers, RulePreview.MaximumDuration, RulePreview.PageSize) = (players, duration, page);
			RuleRunner.Configure(null);
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ASweepRunsOnlyOnTheLeaseHolderAndActsAsTheRule(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			long[] targets = await PlayersAsync(probe, "TargetA", "TargetB", "Other");
			RuleRunner.Configure(Environment(probe));
			PageResponse created = await admin.SendJson("POST", RulesPath, new { name = "sweeper", mode = "enforce", document = Document("sweep", s_target, "ban", null, "found by sweep") }, csrf);
			long id = Json(created).GetProperty("rule").GetProperty("id").GetInt64();

			// a standby copy refuses and writes nothing
			RuleSweeps.Serving = () => false;
			PageResponse standby = await admin.SendJson("POST", $"{RulesPath}/{id}/sweep", new { }, csrf);
			Assert.Equal((503, "service.unavailable"), (standby.Status, AdminArea.ErrorOf(standby)));
			Assert.Empty(await AuditOf(probe, RuleAudit.SweepStart));

			RuleSweeps.Serving = () => true;
			PageResponse started = await admin.SendJson("POST", $"{RulesPath}/{id}/sweep", new { }, csrf);
			Assert.Equal(201, started.Status);
			Assert.Equal(4, Json(started).GetProperty("total").GetInt32());
			await RuleSweeps.WaitAsync(id, TimeSpan.FromSeconds(30));
			JsonElement done = Json(await admin.Get($"{RulesPath}/{id}/sweep"));
			Assert.Equal(("completed", 4, 2), (done.GetProperty("state").GetString(), done.GetProperty("scanned").GetInt32(), done.GetProperty("matched").GetInt32()));
			Assert.True(done.TryGetProperty("finished_at", out JsonElement finished) && finished.ValueKind == JsonValueKind.String);

			await using (AppDbContext db = probe.Open())
			{
				List<AutomatedRuleMatch> matches = await db.AutomatedRuleMatches.AsNoTracking().OrderBy(m => m.UserID).ToListAsync();
				Assert.Equal(targets.Take(2), matches.Select(m => m.UserID));
				Assert.All(matches, m => Assert.Equal((RuleTriggers.Sweep, RuleOutcomes.Enforced), (m.Trigger, m.Outcome)));
				Assert.Equal(targets.Take(2), await db.Sanctions.AsNoTracking().Where(s => s.Type == SanctionTypes.Ban && s.CreatedByKind == AuditActorKinds.Automation).OrderBy(s => s.UserID).Select(s => s.UserID).ToListAsync());
			}
			Assert.Single(await AuditOf(probe, RuleAudit.SweepStart));
			Assert.Equal(404, (await admin.Get($"{RulesPath}/999999/sweep")).Status);
			Assert.Equal(404, (await admin.SendJson("POST", $"{RulesPath}/999999/sweep", new { }, csrf)).Status);
		}
		finally
		{
			RuleSweeps.Serving = () => Failover.RealtimeFailover.Serving;
			RuleRunner.Configure(null);
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ARunningSweepIsCancelledAndAnotherIsRefusedMeanwhile(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		var hold = new TaskCompletionSource();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			await PlayersAsync(probe, "TargetA");
			RuleRunner.Configure(Environment(probe));
			RuleSweeps.Serving = () => true;
			PageResponse created = await admin.SendJson("POST", RulesPath, new { name = "held", document = Document("sweep", s_target) }, csrf);
			long id = Json(created).GetProperty("rule").GetProperty("id").GetInt64();

			// the sweep waits before its first batch until the test lets it go
			RuleSweeps.BeforeBatch = _ => hold.Task;
			Assert.Equal(201, (await admin.SendJson("POST", $"{RulesPath}/{id}/sweep", new { }, csrf)).Status);
			Assert.Equal(409, (await admin.SendJson("POST", $"{RulesPath}/{id}/sweep", new { }, csrf)).Status);
			Assert.Equal("running", Json(await admin.Get($"{RulesPath}/{id}/sweep")).GetProperty("state").GetString());
			Assert.Equal(200, (await admin.SendJson("DELETE", $"{RulesPath}/{id}/sweep", new { }, csrf)).Status);
			hold.SetResult();
			await RuleSweeps.WaitAsync(id, TimeSpan.FromSeconds(30));
			JsonElement cancelled = Json(await admin.Get($"{RulesPath}/{id}/sweep"));
			Assert.Equal(("cancelled", 0), (cancelled.GetProperty("state").GetString(), cancelled.GetProperty("scanned").GetInt32()));
			Assert.Single(await AuditOf(probe, RuleAudit.SweepCancel));
			await using (AppDbContext db = probe.Open())
			{
				Assert.False(await db.AutomatedRuleMatches.AnyAsync());
			}
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
}
