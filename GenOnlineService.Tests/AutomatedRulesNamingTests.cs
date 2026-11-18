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

// The field names of S9 f: every field under an Elastic Common Schema key, the trigger as event.action, and every
// document or match record saved under the earlier keys read and saved in the current names only.
[Collection(AdminAreaCollection.Name)]
public class AutomatedRulesNamingTests
{
	// The earlier key of each field and the current key it reads as, as S9 f names them
	public static readonly (string Old, string Current)[] Expected =
	[
		("device.machine_guid", "host.id"),
		("device.mac", "host.mac"),
		("device.volume_serial", "host.volume_serial"),
		("mac.vendor", "host.mac_vendor"),
		("mac.locally_administered", "host.mac_virtual"),
		("network.ip", "source.ip"),
		("network.country", "source.geo.country_iso_code"),
		("network.city", "source.geo.city_name"),
		("network.asn", "source.as.number"),
		("network.owner", "source.as.organization.name"),
		("account.age_days", "user.age_days"),
		("account.email_domain", "user.email_domain"),
		("account.email_confirmed", "user.email_confirmed"),
		("account.providers", "user.providers"),
		("account.name", "user.name"),
		("account.name_key", "user.name_key"),
		("account.two_factor", "user.two_factor"),
		("sanctions.count", "user.sanctions.count"),
		("sanctions.active_ban", "user.sanctions.active_ban"),
		("sanctions.active_mute", "user.sanctions.active_mute"),
		("reports.open", "user.reports.open"),
		("stats.elo", "user.stats.elo"),
		("stats.games_played", "user.stats.games_played"),
		("event.provider", "event.sign_in_provider"),
		("event.client", "game.client.id"),
		("event.lobby_id", "game.lobby.id"),
		("event.match_id", "game.match.id"),
		("event.won", "game.match.won"),
		("event.desynced", "game.match.desynced"),
		("event.duration_s", "game.match.duration_s"),
		("event.anticheat_kind", "game.anticheat.kind")
	];

	[Fact]
	public void EveryFieldHasACurrentKeyAndItsEarlierKey()
	{
		foreach ((string old, string current) in Expected)
		{
			Assert.Equal(current, RuleFields.CurrentKey(old));
			Assert.Equal(current, RuleFields.Find(old)?.Key);
			Assert.Equal(current, RuleFields.Find(current)?.Key);
		}
		foreach (string kind in AntiCheatReviewKinds.All)
		{
			Assert.Equal("user.anticheat." + kind, RuleFields.CurrentKey("anticheat." + kind));
		}

		// every field but the new event.action came from exactly one earlier key, and no earlier key is still current
		Assert.Equal(RuleFields.All.Select(f => f.Key).Where(k => k != "event.action").Order(), RuleFields.Renamed.Values.Order());
		Assert.DoesNotContain(RuleFields.All, f => RuleFields.Renamed.ContainsKey(f.Key));
		Assert.All(RuleFields.All, f => Assert.Matches(@"^(host|source|user|event|game)\.[a-z_.]+$", f.Key));

		// the trigger reads as event.action with the trigger values, on every trigger
		RuleField action = RuleFields.Find("event.action")!;
		Assert.Equal(RuleTriggers.All, action.Triggers);
		Assert.Equal(["sign_in", "game_session", "lobby_join", "match_result", "anticheat_report", "sweep"], RuleTriggers.All);

		// a yes or no field names both of its states
		Assert.All(RuleFields.All.Where(f => f.Type == RuleFieldTypes.Bool), f => Assert.False(String.IsNullOrEmpty(f.TrueLabel) || String.IsNullOrEmpty(f.FalseLabel), f.Key));
		Assert.Equal(("Email is confirmed", "Email is not confirmed"), (RuleFields.Find("user.email_confirmed")!.TrueLabel, RuleFields.Find("user.email_confirmed")!.FalseLabel));
		Assert.Equal("MAC is virtual", RuleFields.Find("host.mac_virtual")!.TrueLabel);
		Assert.Equal("Two-factor is on", RuleFields.Find("user.two_factor")!.TrueLabel);
	}

	// The document of the earlier names: trigger as its own property and the earlier keys in nested groups
	private static string OldDocument() => JsonSerializer.Serialize(new
	{
		nodes = new object[]
		{
			new { id = "t", kind = "trigger", trigger = "sign_in" },
			new { id = "c", kind = "condition", query = Group("all", Condition("account.age_days", "less_than", 7), Group("any", Condition("account.email_confirmed", "equals", false), Condition("mac.locally_administered", "equals", true))) },
			new { id = "a", kind = "action", action = "flag", reason = "new account" }
		},
		edges = new object[] { new { from = "t", to = "c" }, new { from = "c", to = "a", output = "true" } }
	});

	private static string NewDocument() => JsonSerializer.Serialize(new
	{
		nodes = new object[]
		{
			new { id = "t", kind = "trigger", @event = new { action = "sign_in" } },
			new { id = "c", kind = "condition", query = Group("all", Condition("user.age_days", "less_than", 7), Group("any", Condition("user.email_confirmed", "equals", false), Condition("host.mac_virtual", "equals", true))) },
			new { id = "a", kind = "action", action = "flag", reason = "new account" }
		},
		edges = new object[] { new { from = "t", to = "c" }, new { from = "c", to = "a", output = "true" } }
	});

	[Fact]
	public void AnEarlierDocumentReadsAndSavesInTheCurrentNames()
	{
		(RuleDocument? old, List<RuleProblem> problems) = RuleDocuments.Parse(OldDocument());
		Assert.True(old != null, String.Join("; ", problems));
		(RuleDocument? current, _) = RuleDocuments.Parse(NewDocument());

		string saved = RuleDocuments.Serialize(old!);
		Assert.Equal(RuleDocuments.Serialize(current!), saved);
		Assert.Equal(saved, RuleDocuments.Serialize(RuleDocuments.Deserialize(OldDocument())));
		Assert.Contains("\"event\":{\"action\":\"sign_in\"}", saved);
		Assert.DoesNotContain("\"trigger\":", saved);
		foreach ((string oldKey, _) in Expected)
		{
			Assert.DoesNotContain("\"" + oldKey + "\"", saved);
		}
		Assert.Equal("sign_in", RuleDocuments.TriggerOf(old!));

		// both forms at once are refused, a wrong event.action names its path
		string both = OldDocument().Replace("\"trigger\":\"sign_in\"", "\"trigger\":\"sign_in\",\"event\":{\"action\":\"sweep\"}");
		Assert.Contains(RuleDocuments.Parse(both).Problems, p => p.Path == "nodes[0].trigger");
		Assert.Contains(RuleDocuments.Parse(NewDocument().Replace("\"sign_in\"", "\"bedtime\"")).Problems, p => p.Path == "nodes[0].event.action");
	}

	[Fact]
	public void EarlierFactsReadUnderTheCurrentKeys()
	{
		string facts = RuleFields.CurrentFactsJson("{\"account.name\":[\"Old\"],\"event.anticheat_kind\":[\"modules\"],\"user.age_days\":[2]}");
		JsonElement root = JsonDocument.Parse(facts).RootElement;
		Assert.Equal(["user.name", "game.anticheat.kind", "user.age_days"], root.EnumerateObject().Select(p => p.Name).ToArray());
		Assert.Equal("Old", root.GetProperty("user.name")[0].GetString());
		const string current = "{\"user.name\":[\"New\"]}";
		Assert.Same(current, RuleFields.CurrentFactsJson(current));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AStoredEarlierDocumentEvaluatesTheSameAndSavesInTheCurrentNames(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();
		long newcomer, veteran;
		await using (AppDbContext db = probe.Open())
		{
			newcomer = (await Database.Users.Create(db, "Newcomer")).ID;
			veteran = (await Database.Users.Create(db, "Veteran")).ID;
			await db.Users.Where(u => u.ID == newcomer).ExecuteUpdateAsync(set => set.SetProperty(u => u.CreatedAt, DateTime.UtcNow.AddDays(-2)).SetProperty(u => u.EmailConfirmedAt, (DateTime?)null));
			await db.Users.Where(u => u.ID == veteran).ExecuteUpdateAsync(set => set.SetProperty(u => u.CreatedAt, DateTime.UtcNow.AddDays(-400)).SetProperty(u => u.EmailConfirmedAt, DateTime.UtcNow));

			// a rule a deployed test server saved before the rename, written straight to the table
			db.AutomatedRules.Add(new AutomatedRule { Name = "Old names", Mode = RuleModes.Watch, Enabled = true, CurrentVersion = 1, CreatedBy = "user:1", CreatedAt = DateTime.UtcNow, UpdatedBy = "user:1", UpdatedAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
			long ruleID = await db.AutomatedRules.Select(r => r.ID).SingleAsync();
			db.AutomatedRuleVersions.Add(new AutomatedRuleVersion { RuleID = ruleID, Number = 1, Document = OldDocument(), CreatedBy = "user:1", CreatedAt = DateTime.UtcNow });
			db.AutomatedRuleMatches.Add(new AutomatedRuleMatch { RuleID = ruleID, RuleVersion = 1, UserID = newcomer, Trigger = RuleTriggers.SignIn, MatchedAt = DateTime.UtcNow, Facts = "{\"account.age_days\":[2]}", ReviewState = RuleReviewStates.Pending, Outcome = RuleOutcomes.Flagged, Mode = RuleModes.Flag });
			await db.SaveChangesAsync();
		}

		try
		{
			// the engine compiles it, and it decides as the same rule in the current names does
			await using (AppDbContext db = probe.Open())
			{
				await RuleEngine.ReloadAsync(db);
			}
			CompiledRule stored = Assert.Single(RuleEngine.Current.Rules);
			CompiledRule renamed = CompiledRule.Compile(2, 1, RuleModes.Watch, RuleDocuments.Parse(NewDocument()).Document!);
			foreach (long id in new[] { newcomer, veteran })
			{
				await using AppDbContext db = probe.Open();
				RuleResult a = await stored.RunAsync(new RuleFacts(db, Environment(probe), RuleEvent.Of(RuleTriggers.SignIn, id), DateTime.UtcNow));
				RuleResult b = await renamed.RunAsync(new RuleFacts(db, Environment(probe), RuleEvent.Of(RuleTriggers.SignIn, id), DateTime.UtcNow));
				Assert.Equal(b.Actions.Count, a.Actions.Count);
				Assert.Equal(b.FieldsRead.Order(), a.FieldsRead.Order());
			}
			Assert.Equal(1, (await stored.RunAsync(new RuleFacts(probe.Open(), Environment(probe), RuleEvent.Of(RuleTriggers.SignIn, newcomer), DateTime.UtcNow))).Actions.Count);

			// the read shows the current names, and saving it again stores only those
			long rule = await ReadRuleIDAsync(probe);
			JsonElement detail = Json(await admin.Get(RulesPath + "/" + rule));
			string shown = detail.GetProperty("document").GetRawText();
			Assert.Contains("\"user.age_days\"", shown);
			Assert.DoesNotContain("account.age_days", shown);
			Assert.Equal("sign_in", detail.GetProperty("document").GetProperty("nodes")[0].GetProperty("event").GetProperty("action").GetString());
			Assert.Equal("sign_in", detail.GetProperty("rule").GetProperty("trigger").GetString());

			PageResponse saved = await admin.SendJson("POST", RulesPath + "/" + rule + "/versions", new { document = JsonDocument.Parse(OldDocument()).RootElement, base_version = 1 }, csrf);
			Assert.Equal(201, saved.Status);
			await using (AppDbContext db = probe.Open())
			{
				// a JSON column may store the keys in its own order and spacing
				string stored2 = await db.AutomatedRuleVersions.Where(v => v.RuleID == rule && v.Number == 2).Select(v => v.Document).SingleAsync();
				Assert.DoesNotMatch(@"""trigger""\s*:", stored2);
				Assert.All(Expected, e => Assert.DoesNotContain("\"" + e.Old + "\"", stored2));
				Assert.Contains("\"user.age_days\"", stored2);
				Assert.Equal(RuleDocuments.Serialize(RuleDocuments.Parse(NewDocument()).Document!), RuleDocuments.Serialize(RuleDocuments.Deserialize(stored2)));
			}

			// a match recorded under the earlier keys reads under the current ones
			JsonElement matches = Json(await admin.Get(RulesPath + "/matches?rule_id=" + rule));
			JsonElement facts = matches.GetProperty("matches")[0].GetProperty("facts");
			Assert.Equal(2, facts.GetProperty("user.age_days")[0].GetInt32());
			Assert.False(facts.TryGetProperty("account.age_days", out _));
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	private static async Task<long> ReadRuleIDAsync(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		return await db.AutomatedRules.Select(r => r.ID).SingleAsync();
	}
}
