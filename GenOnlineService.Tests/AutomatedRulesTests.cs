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
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The automated rules of S3: the staff routes under rules.manage with their audit rows and immutable versions, the
// validation of a document on save, the field catalogue, every operator family through the compiled rule, device ids
// matched through their hash, and the reload of every copy. The rule set is process-wide, so the class runs with the
// other process-wide tests and empties it at the end.
[Collection(AdminAreaCollection.Name)]
public class AutomatedRulesTests
{
	public const string RulesPath = "/api/v1/staff/rules";

	internal static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role)
	{
		// Program registers the reload of the rules at start
		Catalog.CatalogSignals.Register(Catalog.CatalogResources.Rules, RuleEngine.ReloadAsync);
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role));
	}

	internal static async Task<List<AuditLogEntry>> AuditOf(ProbeDatabase probe, string action)
	{
		await using AppDbContext db = probe.Open();
		return await db.AuditLog.AsNoTracking().Where(e => e.Action == action).OrderBy(e => e.ID).ToListAsync();
	}

	internal static JsonElement Json(PageResponse response) => JsonDocument.Parse(response.Body).RootElement;

	// trigger, one condition and an action on its true output
	internal static object Document(string trigger, object query, string action = "flag", long? durationS = null, string reason = "matched by test") => new
	{
		nodes = new object[]
		{
			new { id = "t", kind = "trigger", trigger },
			new { id = "c", kind = "condition", query },
			new { id = "a", kind = "action", action, duration_s = durationS, reason }
		},
		edges = new object[]
		{
			new { from = "t", to = "c" },
			new { from = "c", to = "a", output = "true" }
		}
	};

	internal static object Condition(string field, string op, object? value = null) => new { field, @operator = op, value };

	internal static object Group(string match, params object[] rules) => new { match, rules };

	internal static void InitializeDeviceHasher() => DeviceHasher.Initialize(Path.Combine(Path.GetTempPath(), "gn_probe_device_hmac.key"));

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffManageRulesWithImmutableVersionsAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
			Dictionary<string, string> csrf = await admin.Csrf();

			// moderators hold no rules.manage
			Assert.Equal(403, (await moderator.Get(RulesPath)).Status);
			Assert.Equal(403, (await moderator.SendJson("POST", RulesPath, new { name = "x" }, await moderator.Csrf())).Status);

			object first = Document("sign_in", Condition("account.email_domain", "equals", "spam.example"));
			PageResponse created = await admin.SendJson("POST", RulesPath, new { name = "  Spam domain ", document = first, comment = "first cut" }, csrf);
			Assert.Equal(201, created.Status);
			JsonElement rule = Json(created).GetProperty("rule");
			long id = rule.GetProperty("id").GetInt64();
			Assert.Equal(("Spam domain", "watch", false, 1, "sign_in"), (rule.GetProperty("name").GetString(), rule.GetProperty("mode").GetString(), rule.GetProperty("enabled").GetBoolean(), rule.GetProperty("current_version").GetInt32(), rule.GetProperty("trigger").GetString()));
			Assert.Equal("user:" + chief.ID, rule.GetProperty("created_by").GetString());
			AuditLogEntry createdEntry = Assert.Single(await AuditOf(probe, RuleAudit.Create));
			Assert.Equal((AuditActorKinds.User, (long?)chief.ID, RuleAudit.TargetKind, (long?)id), (createdEntry.ActorKind, createdEntry.ActorID, createdEntry.TargetKind, createdEntry.TargetID));

			// a new version keeps the old one as it was
			object second = Document("sign_in", Condition("account.email_domain", "in_list", new[] { "spam.example", "junk.example" }), "ban", null, "spam domain");
			PageResponse saved = await admin.SendJson("POST", $"{RulesPath}/{id}/versions", new { document = second, comment = "both domains", base_version = 1 }, csrf);
			Assert.Equal(201, saved.Status);
			Assert.Equal(2, Json(saved).GetProperty("rule").GetProperty("current_version").GetInt32());
			Assert.Equal([2, 1], Json(saved).GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("number").GetInt32()).ToArray());
			Assert.Equal("ban", Json(saved).GetProperty("document").GetProperty("nodes")[2].GetProperty("action").GetString());
			JsonElement old = Json(await admin.Get($"{RulesPath}/{id}/versions/1"));
			Assert.Equal(("first cut", "equals"), (old.GetProperty("comment").GetString(), old.GetProperty("document").GetProperty("nodes")[1].GetProperty("query").GetProperty("operator").GetString()));
			Assert.Equal(2, JsonDocument.Parse(Assert.Single(await AuditOf(probe, RuleAudit.Version)).Details!).RootElement.GetProperty("version").GetInt32());

			// a save from a stale version is refused and changes nothing
			PageResponse stale = await admin.SendJson("POST", $"{RulesPath}/{id}/versions", new { document = first, base_version = 1 }, csrf);
			Assert.Equal((409, "conflict"), (stale.Status, AdminArea.ErrorOf(stale)));
			Assert.Single(await AuditOf(probe, RuleAudit.Version));

			PageResponse updated = await admin.Patch($"{RulesPath}/{id}", new { mode = "enforce", enabled = true }, csrf);
			Assert.Equal((200, "enforce", true), (updated.Status, Json(updated).GetProperty("mode").GetString(), Json(updated).GetProperty("enabled").GetBoolean()));
			Assert.Contains("\"from\"", Assert.Single(await AuditOf(probe, RuleAudit.Update)).Details);
			Assert.Equal(400, (await admin.Patch($"{RulesPath}/{id}", new { mode = "nuke" }, csrf)).Status);
			Assert.Equal(404, (await admin.Patch($"{RulesPath}/999999", new { enabled = true }, csrf)).Status);

			// this copy runs the enabled rule at once
			CompiledRule live = Assert.Single(RuleEngine.Current.For(RuleTriggers.SignIn));
			Assert.Equal((id, 2, RuleModes.Enforce), (live.RuleID, live.Version, live.Mode));

			Assert.Equal(200, (await admin.SendJson("POST", $"{RulesPath}/{id}/archive", new { }, csrf)).Status);
			Assert.Empty(Json(await admin.Get(RulesPath)).GetProperty("rules").EnumerateArray());
			Assert.Single(Json(await admin.Get(RulesPath + "?archived=true")).GetProperty("rules").EnumerateArray());
			Assert.Empty(RuleEngine.Current.Rules);
			Assert.Equal(200, (await admin.SendJson("POST", $"{RulesPath}/{id}/restore", new { }, csrf)).Status);
			Assert.Single(Json(await admin.Get(RulesPath)).GetProperty("rules").EnumerateArray());
			Assert.Single(RuleEngine.Current.Rules);
			Assert.Equal((1, 1), ((await AuditOf(probe, RuleAudit.Archive)).Count, (await AuditOf(probe, RuleAudit.Restore)).Count));

			// without the antiforgery header nothing changes
			Assert.Equal(400, (await admin.SendJson("POST", $"{RulesPath}/{id}/archive", new { }, null)).Status);
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ADocumentWithProblemsIsRefusedWithEveryPath(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();

		async Task<List<(string Path, string Reason)>> Problems(object document)
		{
			PageResponse response = await admin.SendJson("POST", RulesPath, new { name = "bad", document }, csrf);
			Assert.Equal((400, "invalid_argument"), (response.Status, AdminArea.ErrorOf(response)));
			JsonElement parameters = Json(response).GetProperty("params");
			Assert.Equal("document", parameters.GetProperty("field").GetString());
			return parameters.GetProperty("problems").EnumerateArray().Select(p => (p.GetProperty("path").GetString()!, p.GetProperty("reason").GetString()!)).ToList();
		}

		Assert.Contains(("document", "missing"), await Problems(null!));
		Assert.Contains(await Problems(Document("sign_in", Condition("account.nope", "equals", "x"))), p => p.Path == "nodes[1].query.field" && p.Reason == "not a known field");
		Assert.Contains(await Problems(Document("sign_in", Condition("event.lobby_id", "equals", 4))), p => p.Path == "nodes[1].query.field" && p.Reason.Contains("sign_in"));
		Assert.Contains(await Problems(Document("sign_in", Condition("account.age_days", "contains", "4"))), p => p.Path == "nodes[1].query.operator");
		Assert.Contains(await Problems(Document("sign_in", Group("all", Condition("account.age_days", "greater_than", "old")))), p => p.Path == "nodes[1].query.rules[0].value" && p.Reason == "a number");
		Assert.Contains(await Problems(Document("sign_in", Condition("account.age_days", "between", new[] { 9, 2 }))), p => p.Path == "nodes[1].query.value");
		Assert.Contains(await Problems(Document("sign_in", Condition("network.ip", "cidr", "nonsense"))), p => p.Path == "nodes[1].query.value");
		Assert.Contains(await Problems(Document("sign_in", Group("some", Condition("account.two_factor", "equals", true)))), p => p.Path == "nodes[1].query.match");
		Assert.Contains(await Problems(Document("sign_in", Condition("account.two_factor", "equals", true), "mute")), p => p.Path == "nodes[2].duration_s");
		Assert.Contains(await Problems(Document("sign_in", Condition("account.two_factor", "equals", true), "kick", 600)), p => p.Path == "nodes[2].duration_s");
		Assert.Contains(await Problems(Document("sign_in", Condition("account.two_factor", "equals", true), "ban", 90)), p => p.Path == "nodes[2].duration_s");
		Assert.Contains(await Problems(Document("sign_in", Condition("account.two_factor", "equals", true), "ban", null, " ")), p => p.Path == "nodes[2].reason");
		Assert.Contains(await Problems(Document("bedtime", Condition("account.two_factor", "equals", true))), p => p.Path == "nodes[0].event.action");
		Assert.Contains(await Problems(new { nodes = Array.Empty<object>(), edges = Array.Empty<object>() }), p => p.Path == "nodes");
		Assert.Contains(await Problems(new { nodes = "x" }), p => p.Path == "nodes");

		// two triggers, a step node, a loop and a node the trigger never reaches
		object shapes = new
		{
			nodes = new object[]
			{
				new { id = "t", kind = "trigger", trigger = "sign_in" },
				new { id = "t2", kind = "trigger", trigger = "sweep" },
				new { id = "s", kind = "step" }
			},
			edges = Array.Empty<object>()
		};
		List<(string Path, string Reason)> found = await Problems(shapes);
		Assert.Contains(("nodes", "exactly one trigger node"), found);
		Assert.Contains(("nodes[2].kind", "step nodes are not supported yet"), found);
		object loop = new
		{
			nodes = new object[]
			{
				new { id = "t", kind = "trigger", trigger = "sign_in" },
				new { id = "c1", kind = "condition", query = Condition("account.two_factor", "equals", true) },
				new { id = "c2", kind = "condition", query = Condition("account.two_factor", "equals", false) },
				new { id = "a", kind = "action", action = "flag", reason = "r" }
			},
			edges = new object[]
			{
				new { from = "t", to = "c1" },
				new { from = "c1", to = "c2", output = "true" },
				new { from = "c2", to = "c1", output = "true" },
				new { from = "c2", to = "a", output = "false" }
			}
		};
		Assert.Contains(("edges", "the edges form a loop"), await Problems(loop));
		object stray = new
		{
			nodes = new object[]
			{
				new { id = "t", kind = "trigger", trigger = "sign_in" },
				new { id = "c", kind = "condition", query = Condition("account.two_factor", "equals", true) },
				new { id = "a", kind = "action", action = "flag", reason = "r" }
			},
			edges = new object[] { new { from = "t", to = "c" }, new { from = "c", to = "a", output = "maybe" } }
		};
		Assert.Contains(("edges[1].output", "true or false from a condition"), await Problems(stray));
		object unreached = new
		{
			nodes = new object[]
			{
				new { id = "t", kind = "trigger", trigger = "sign_in" },
				new { id = "a", kind = "action", action = "flag", reason = "r" }
			},
			edges = Array.Empty<object>()
		};
		List<(string Path, string Reason)> lonely = await Problems(unreached);
		Assert.Contains(("nodes[1]", "not reached from the trigger"), lonely);
		Assert.Contains(("nodes", "no action is reached from the trigger"), lonely);

		Assert.Empty(await AuditOf(probe, RuleAudit.Create));
		await using (AppDbContext db = probe.Open())
		{
			Assert.False(await db.AutomatedRules.AnyAsync());
		}

		// the check alone answers both ways and writes nothing
		JsonElement bad = Json(await admin.SendJson("POST", RulesPath + "/validate", new { document = unreached }, csrf));
		Assert.False(bad.GetProperty("valid").GetBoolean());
		Assert.Contains(bad.GetProperty("problems").EnumerateArray(), p => p.GetProperty("path").GetString() == "nodes[1]");
		JsonElement good = Json(await admin.SendJson("POST", RulesPath + "/validate", new { document = Document("lobby_join", Condition("event.lobby_id", "equals", 4)) }, csrf));
		Assert.True(good.GetProperty("valid").GetBoolean());
		Assert.Empty(good.GetProperty("problems").EnumerateArray());
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheCatalogueNamesEveryFieldWithItsTriggersAndOperators(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		JsonElement catalogue = Json(await admin.Get(RulesPath + "/fields"));
		Dictionary<string, JsonElement> fields = catalogue.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);

		foreach ((string old, string key) in AutomatedRulesNamingTests.Expected)
		{
			Assert.True(fields.ContainsKey(key), key);
			Assert.False(fields.ContainsKey(old), old);
		}
		Assert.True(fields.ContainsKey("user.anticheat.modules"));
		Assert.True(fields.ContainsKey("event.action"));
		Assert.Equal(["equals", "not_equals", "in_list", "is_empty"], fields["host.mac"].GetProperty("operators").EnumerateArray().Select(o => o.GetString()!).ToArray());
		Assert.Contains("cidr", fields["source.ip"].GetProperty("operators").EnumerateArray().Select(o => o.GetString()));
		Assert.Equal(RuleTriggers.All, fields["user.age_days"].GetProperty("triggers").EnumerateArray().Select(t => t.GetString()!).ToArray());
		Assert.Equal(RuleTriggers.All, fields["event.action"].GetProperty("triggers").EnumerateArray().Select(t => t.GetString()!).ToArray());
		Assert.Equal(["lobby_join"], fields["game.lobby.id"].GetProperty("triggers").EnumerateArray().Select(t => t.GetString()!).ToArray());
		Assert.Equal(("Email is confirmed", "Email is not confirmed"), (fields["user.email_confirmed"].GetProperty("true_label").GetString(), fields["user.email_confirmed"].GetProperty("false_label").GetString()));
		Assert.Contains(catalogue.GetProperty("renamed").EnumerateArray(), r => r.GetProperty("from").GetString() == "account.age_days" && r.GetProperty("to").GetString() == "user.age_days");
		Assert.Equal(["watch", "flag", "enforce"], catalogue.GetProperty("modes").EnumerateArray().Select(m => m.GetString()!).ToArray());
	}

	// A GeoIP lookup that knows one address
	internal sealed class KnownAddresses : IAddressDirectory
	{
		public AddressFacts? Lookup(string ipAddress) => ipAddress == "203.0.113.9" ? new AddressFacts("NL", "Amsterdam", 64500, "Example Hosting") : null;
	}

	internal static RuleEnvironment Environment(ProbeDatabase probe, IAddressDirectory? addresses = null) =>
		new RuleEnvironment(new ProbeFactory(probe), addresses ?? new KnownAddresses(), OuiRegistry.Parse(["001A2B\tExample Networks"]));

	private static async Task<bool> MatchesAsync(ProbeDatabase probe, long userID, object query, string trigger = "sign_in", string? ip = null, params (string, object?)[] fields)
	{
		string json = JsonSerializer.Serialize(Document(trigger, query));
		(RuleDocument? document, List<RuleProblem> problems) = RuleDocuments.Parse(json);
		Assert.True(document != null, String.Join("; ", problems));
		CompiledRule rule = CompiledRule.Compile(1, 1, RuleModes.Watch, document!);
		await using AppDbContext db = probe.Open();
		var facts = new RuleFacts(db, Environment(probe), RuleEvent.Of(trigger, userID, ip, fields), DateTime.UtcNow);
		return (await rule.RunAsync(facts)).Actions.Count > 0;
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EveryOperatorFamilyDecidesOnThePlayersFacts(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		long id;
		await using (AppDbContext db = probe.Open())
		{
			User user = await Database.Users.Create(db, "Sneaky");
			id = user.ID;
			await db.Users.Where(u => u.ID == id).ExecuteUpdateAsync(set => set
				.SetProperty(u => u.Email, "Sneaky@Spam.Example")
				.SetProperty(u => u.CreatedAt, DateTime.UtcNow.AddDays(-3)));
			db.Devices.Add(new Device { UserID = id, Kind = DeviceKinds.MachineGuid, ValueHmac = DeviceHasher.HashRaw("AB12-CD34-YY"), ValueRaw = "AB12-CD34-YY", FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
			db.Devices.Add(new Device { UserID = id, Kind = DeviceKinds.Mac, ValueHmac = DeviceHasher.HashRaw("00:1A:2B:3C:4D:5EZZ"), ValueRaw = "00:1A:2B:3C:4D:5EZZ", FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
			db.Sanctions.Add(new Sanction { UserID = id, Type = SanctionTypes.Kick, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
		}

		// text, any case
		Assert.True(await MatchesAsync(probe, id, Condition("account.email_domain", "equals", "SPAM.example")));
		Assert.True(await MatchesAsync(probe, id, Condition("account.email_domain", "in_list", new[] { "a.example", "spam.example" })));
		Assert.True(await MatchesAsync(probe, id, Condition("account.name", "contains", "eak")));
		Assert.True(await MatchesAsync(probe, id, Condition("account.name", "starts_with", "sne")));
		Assert.False(await MatchesAsync(probe, id, Condition("account.email_domain", "not_equals", "spam.example")));
		Assert.True(await MatchesAsync(probe, id, Condition("network.city", "is_empty")));

		// numbers
		Assert.True(await MatchesAsync(probe, id, Condition("account.age_days", "less_than", 7)));
		Assert.False(await MatchesAsync(probe, id, Condition("account.age_days", "greater_than", 7)));
		Assert.True(await MatchesAsync(probe, id, Condition("account.age_days", "between", new[] { 3, 3 })));
		Assert.True(await MatchesAsync(probe, id, Condition("sanctions.count", "equals", 1)));
		Assert.True(await MatchesAsync(probe, id, Condition("stats.elo", "is_empty")));

		// true and false, groups
		Assert.True(await MatchesAsync(probe, id, Condition("account.email_confirmed", "equals", false)));
		Assert.True(await MatchesAsync(probe, id, Group("any", Condition("account.two_factor", "equals", true), Group("all", Condition("sanctions.active_ban", "equals", false), Condition("reports.open", "equals", 0)))));
		Assert.False(await MatchesAsync(probe, id, Group("all", Condition("account.two_factor", "equals", true), Condition("reports.open", "equals", 0))));

		// addresses: the event's own, a CIDR range and the GeoIP facts of it
		Assert.True(await MatchesAsync(probe, id, Condition("network.ip", "cidr", "203.0.113.0/24"), ip: "::ffff:203.0.113.9"));
		Assert.True(await MatchesAsync(probe, id, Condition("network.ip", "equals", "203.0.113.9"), ip: "203.0.113.9"));
		Assert.False(await MatchesAsync(probe, id, Condition("network.ip", "cidr", "198.51.100.0/24"), ip: "203.0.113.9"));
		Assert.True(await MatchesAsync(probe, id, Group("all", Condition("network.country", "equals", "nl"), Condition("network.asn", "equals", 64500), Condition("network.owner", "contains", "hosting")), ip: "203.0.113.9"));

		// device ids as the client sends them, typed in any case or copied as the whole key, and the vendor of the MAC block
		Assert.True(await MatchesAsync(probe, id, Condition("device.machine_guid", "equals", "AB12-CD34-YY")));
		Assert.True(await MatchesAsync(probe, id, Condition("device.machine_guid", "in_list", new[] { "nothing", "ab12-cd34-yy" })));
		Assert.True(await MatchesAsync(probe, id, Condition("device.machine_guid", "equals", Database.StaffInspection.KeyOf(DeviceHasher.HashRaw("AB12-CD34-YY")).ToUpperInvariant())));
		Assert.False(await MatchesAsync(probe, id, Condition("device.machine_guid", "equals", "AB12-CD34-YZ")));
		Assert.True(await MatchesAsync(probe, id, Condition("device.machine_guid", "not_equals", "AB12-CD34-YZ")));
		Assert.True(await MatchesAsync(probe, id, Condition("device.volume_serial", "is_empty")));
		Assert.True(await MatchesAsync(probe, id, Condition("mac.vendor", "equals", "example networks")));
		Assert.True(await MatchesAsync(probe, id, Condition("mac.locally_administered", "equals", false)));

		// event fields come from the event
		Assert.True(await MatchesAsync(probe, id, Condition("event.lobby_id", "equals", 42), "lobby_join", null, (RuleEventFields.LobbyId, 42L)));
		Assert.False(await MatchesAsync(probe, id, Condition("event.lobby_id", "equals", 42), "lobby_join", null, (RuleEventFields.LobbyId, 43L)));
	}

	// Each read of a field is kept for the evaluation and only the fields a branch reads are loaded
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task FactsLoadOnlyForTheFieldsARuleReads(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		long id;
		await using (AppDbContext db = probe.Open())
		{
			id = (await Database.Users.Create(db, "Plain")).ID;
		}
		object query = Group("any", Condition("account.two_factor", "equals", false), Condition("reports.open", "greater_than", 3));
		(RuleDocument? document, _) = RuleDocuments.Parse(JsonSerializer.Serialize(Document("sign_in", query)));
		CompiledRule rule = CompiledRule.Compile(1, 1, RuleModes.Watch, document!);
		await using AppDbContext context = probe.Open();
		var facts = new RuleFacts(context, Environment(probe), RuleEvent.Of("sign_in", id), DateTime.UtcNow);
		RuleResult result = await rule.RunAsync(facts);
		Assert.Single(result.Actions);
		Assert.Equal(["user.two_factor"], result.FieldsRead);
		Assert.Equal(["user.two_factor"], facts.Values.Keys.ToArray());
	}

	// Another copy saves a change: this copy follows on its next reload, an unchanged set is kept as it is
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EveryCopyReloadsTheRulesAnotherCopyChanged(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		InitializeDeviceHasher();
		try
		{
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			PageResponse created = await admin.SendJson("POST", RulesPath, new { name = "r", enabled = true, document = Document("lobby_join", Condition("event.lobby_id", "equals", 1)) }, await admin.Csrf());
			long id = Json(created).GetProperty("rule").GetProperty("id").GetInt64();
			RuleSet loaded = RuleEngine.Current;
			Assert.Equal(RuleTriggers.LobbyJoin, Assert.Single(loaded.Rules).Trigger);

			await using (AppDbContext db = probe.Open())
			{
				await RuleEngine.ReloadAsync(db);
			}
			Assert.Same(loaded, RuleEngine.Current);

			// another copy switches the rule to enforce and saves a version for another trigger
			await using (AppDbContext db = probe.Open())
			{
				db.AutomatedRuleVersions.Add(new AutomatedRuleVersion { RuleID = id, Number = 2, Document = JsonSerializer.Serialize(Document("match_result", Condition("event.won", "equals", true))), CreatedBy = "copy-b", CreatedAt = DateTime.UtcNow });
				await db.SaveChangesAsync();
				await db.AutomatedRules.Where(r => r.ID == id).ExecuteUpdateAsync(set => set.SetProperty(r => r.CurrentVersion, 2).SetProperty(r => r.Mode, RuleModes.Enforce));
			}
			Assert.True(RuleEngine.Current.Has(RuleTriggers.LobbyJoin));
			await using (AppDbContext db = probe.Open())
			{
				await RuleEngine.ReloadAsync(db);
			}
			CompiledRule now = Assert.Single(RuleEngine.Current.Rules);
			Assert.Equal((RuleTriggers.MatchResult, 2, RuleModes.Enforce), (now.Trigger, now.Version, now.Mode));
			Assert.False(RuleEngine.Current.Has(RuleTriggers.LobbyJoin));
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}
}
