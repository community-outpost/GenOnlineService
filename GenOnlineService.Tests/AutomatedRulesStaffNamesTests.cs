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

// The names staff read beside a rule: who saved a rule and its versions as a player, and the rule and version that made
// a sanction on the player page
[Collection(AdminAreaCollection.Name)]
public class AutomatedRulesStaffNamesTests
{
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SavedByReadsAsThePlayerWhoSaved(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();
		try
		{
			PageResponse created = await admin.SendJson("POST", RulesPath, new { name = "Spam", document = Document("sign_in", Condition("user.email_domain", "equals", "spam.example")) }, csrf);
			Assert.Equal(201, created.Status);
			long id = Json(created).GetProperty("rule").GetProperty("id").GetInt64();

			void IsChief(JsonElement player)
			{
				Assert.Equal(chief.ID, player.GetProperty("id").GetInt64());
				Assert.Equal("Chief", player.GetProperty("name").GetString());
			}

			JsonElement detail = Json(await admin.Get(RulesPath + "/" + id));
			Assert.Equal("user:" + chief.ID, detail.GetProperty("rule").GetProperty("created_by").GetString());
			IsChief(detail.GetProperty("rule").GetProperty("created_by_player"));
			IsChief(detail.GetProperty("rule").GetProperty("updated_by_player"));
			IsChief(detail.GetProperty("versions")[0].GetProperty("created_by_player"));
			IsChief(Json(await admin.Get(RulesPath)).GetProperty("rules")[0].GetProperty("updated_by_player"));
			IsChief(Json(await admin.Get(RulesPath + "/" + id + "/versions/1")).GetProperty("created_by_player"));

			// an actor that is no player, such as the circuit breaker, has no player
			await using (AppDbContext db = probe.Open())
			{
				await db.AutomatedRules.Where(r => r.ID == id).ExecuteUpdateAsync(set => set.SetProperty(r => r.UpdatedBy, "rule:" + id + "@v1"));
			}
			JsonElement tripped = Json(await admin.Get(RulesPath + "/" + id)).GetProperty("rule");
			Assert.Equal(JsonValueKind.Null, tripped.GetProperty("updated_by_player").ValueKind);
			IsChief(tripped.GetProperty("created_by_player"));
		}
		finally
		{
			RuleEngine.Install(RuleSet.Empty);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ARuleMadeSanctionNamesTheRuleAndVersionOnThePlayerPage(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		long target;
		await using (AppDbContext db = probe.Open())
		{
			target = (await Database.Users.Create(db, "Suspect")).ID;
		}
		await using (AppDbContext db = probe.Open())
		{
			CompiledRule rule = CompiledRule.Compile(7, 3, RuleModes.Enforce, RuleDocuments.Parse(JsonSerializer.Serialize(Document("sign_in", Condition("user.two_factor", "equals", false), "ban"))).Document!);
			await Database.Moderation.AddSanction(db, target, SanctionTypes.Ban, "Ban evasion", null, rule.Actor);
		}
		await using (AppDbContext db = probe.Open())
		{
			await Database.Moderation.AddSanction(db, target, SanctionTypes.Kick, "Spam", null, new Database.ModerationActor { Kind = AuditActorKinds.User, UserID = chief.ID, Name = "user:" + chief.ID, Source = ModerationSources.Api });
		}

		JsonElement sanctions = Json(await admin.Get($"/api/v1/players/{target}/staff_view")).GetProperty("sanctions");
		JsonElement ban = sanctions.EnumerateArray().Single(s => s.GetProperty("type").GetString() == "ban");
		Assert.Equal(("automation", "rule:7@v3"), (ban.GetProperty("created_by_kind").GetString(), ban.GetProperty("created_by_name").GetString()));
		Assert.Equal(JsonValueKind.Null, ban.GetProperty("created_by").ValueKind);
		JsonElement kick = sanctions.EnumerateArray().Single(s => s.GetProperty("type").GetString() == "kick");
		Assert.Equal("user", kick.GetProperty("created_by_kind").GetString());
		Assert.Equal(chief.ID, kick.GetProperty("created_by").GetProperty("id").GetInt64());
	}
}
