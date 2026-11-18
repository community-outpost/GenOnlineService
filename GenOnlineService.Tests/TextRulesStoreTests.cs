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

using GenOnlineService.Realtime;
using GenOnlineService.TextRules;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The stored lists (S7): the seed on the first start, the reload of every copy on a new revision, the staff routes under
// textrules.manage with their audit rows and validation, and the name checks of sign-up and rename on the stored lists.
// The routes rebuild the process-wide lists, so the class runs with the other process-wide tests and restores the seed lists.
[Collection(AdminAreaCollection.Name)]
public class TextRulesStoreTests
{
	private const string TermsPath = "/api/v1/staff/text_rules/terms";
	private const string ExceptionsPath = "/api/v1/staff/text_rules/exceptions";
	private const string WorkflowsPath = "/api/v1/staff/text_rules/workflows";
	private const string TestPath = "/api/v1/staff/text_rules/test";

	private static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role));
	}

	private static async Task<List<AuditLogEntry>> AuditOf(ProbeDatabase probe, string action)
	{
		await using AppDbContext db = probe.Open();
		return await db.AuditLog.AsNoTracking().Where(e => e.Action == action).OrderBy(e => e.ID).ToListAsync();
	}

	private static async Task SeedAsync(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		Assert.True(await TextChecks.SeedAsync(db));
		await TextChecks.ReloadAsync(db);
	}

	private static JsonElement Json(PageResponse response) => JsonDocument.Parse(response.Body).RootElement;

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheSeedIsWrittenOnceAndEveryCopyReloadsANewRevision(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		try
		{
			await SeedAsync(probe);
			await using (AppDbContext db = probe.Open())
			{
				Assert.False(await TextChecks.SeedAsync(db));
				Assert.Equal(TextRuleSeeds.Terms().Count, await db.TextRuleTerms.CountAsync());
				Assert.Equal(TextRuleSeeds.Exceptions().Count, await db.TextRuleExceptions.CountAsync());
				Assert.Equal(TextKinds.All.Count, await db.TextRuleWorkflows.CountAsync());
				Assert.Equal(ProfileActions.ReservedNameWords.Count, await db.TextRuleTerms.CountAsync(t => t.Source == TextRuleSources.Reserved));
			}
			Assert.Single(await AuditOf(probe, TextRuleAudit.Seed));
			TextRuleSet seeded = TextChecks.Current;
			Assert.NotEqual(String.Empty, seeded.Revision);
			Assert.Equal("ronin", ProfileActions.ReservedWordIn("TheRonin"));
			Assert.Equal("\u0634\u0631\u0645\u0648\u0637\u0629", ProfileActions.ReservedWordIn("\u0634\u0631\u0645\u0640\u0648\u0637\u0629"));

			// an unchanged revision keeps the built lists
			await using (AppDbContext db = probe.Open())
			{
				await TextChecks.ReloadAsync(db);
			}
			Assert.Same(seeded, TextChecks.Current);

			// another copy changes the lists: this copy follows on its next reload
			await using (AppDbContext db = probe.Open())
			{
				db.TextRuleTerms.Add(new TextRuleTerm { Term = "zorgblat", Language = TextLanguages.English, Category = TextCategories.Insult, Severity = TextSeverities.Block, Match = TextMatchModes.Word, UpdatedBy = "copy-b", UpdatedAt = DateTime.UtcNow });
				await TextChecks.TouchAsync(db, "copy-b");
				await db.SaveChangesAsync();
			}
			Assert.Null(ProfileActions.ReservedWordIn("Zorgblat"));
			await using (AppDbContext db = probe.Open())
			{
				await TextChecks.ReloadAsync(db);
			}
			Assert.Equal("zorgblat", ProfileActions.ReservedWordIn("Zorgblat"));
		}
		finally
		{
			TextChecks.UseDefaults();
		}
	}

	// Sign-up, rename and the name choice refuse a name with a listed word as name.reserved, as for the plugin's words
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignUpAndRenameRefuseAListedWord(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		try
		{
			await SeedAsync(probe);
			PageResponse refused = await PasswordPagesTests.SignUp(new AccountsBrowser(probe, PasswordPagesTests.Enabled()), "Sh4rmoota", "bad@example.org", "a long enough password");
			Assert.Equal(400, refused.Status);
			Assert.Contains("This name contains a reserved word.", refused.Body);
			Assert.Null(await PasswordPagesTests.UserByEmail(probe, "bad@example.org"));
			Assert.Equal(302, (await PasswordPagesTests.SignUp(new AccountsBrowser(probe, PasswordPagesTests.Enabled()), "Scunthorpe", "fine@example.org", "a long enough password")).Status);

			User user = (await PasswordPagesTests.UserByEmail(probe, "fine@example.org"))!;
			var names = new Names.PlayerNames(new ProbeFactory(probe));
			Result<Empty> renamed = await names.RenameSelfAsync(user.ID, "F.u.c.k");
			Assert.Equal(ErrorCodes.NameReserved, renamed.Code);
			Assert.Equal("fuck", JsonSerializer.SerializeToElement(renamed.Params, Api.ApiResults.Json).GetProperty("word").GetString());
			Assert.Equal(ErrorCodes.NameReserved, (await names.RenameSelfAsync(user.ID, "TheAdmin")).Code);
			Assert.True((await names.RenameSelfAsync(user.ID, "Peacock")).Ok);
		}
		finally
		{
			TextChecks.UseDefaults();
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffManageTheWordsAndExceptionsAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		try
		{
			await SeedAsync(probe);
			(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);

			// moderators hold no textrules.manage
			Assert.Equal(403, (await moderator.Get(TermsPath)).Status);
			Assert.Equal(403, (await moderator.SendJson("POST", TestPath, new { kind = "chat", text = "hi" }, await moderator.Csrf())).Status);

			JsonElement list = Json(await admin.Get(TermsPath + "?language=ar&category=sexual&query=" + Uri.EscapeDataString("\u0634\u0631\u0645")));
			Assert.Contains(list.GetProperty("terms").EnumerateArray(), t => t.GetProperty("term").GetString() == "\u0634\u0631\u0645\u0648\u0637\u0629");
			Assert.All(list.GetProperty("terms").EnumerateArray(), t => Assert.Equal("ar", t.GetProperty("language").GetString()));
			Assert.Equal(list.GetProperty("terms").GetArrayLength(), list.GetProperty("total").GetInt32());

			Dictionary<string, string> csrf = await admin.Csrf();
			PageResponse added = await admin.SendJson("POST", TermsPath, new { term = "  Zorg   Blat ", language = "en", category = "insult", severity = "flag", match = "word" }, csrf);
			Assert.Equal(201, added.Status);
			long id = Json(added).GetProperty("id").GetInt64();
			Assert.Equal(("zorg blat", "staff", "user:" + chief.ID), (Json(added).GetProperty("term").GetString(), Json(added).GetProperty("source").GetString(), Json(added).GetProperty("updated_by").GetString()));
			AuditLogEntry entry = Assert.Single(await AuditOf(probe, TextRuleAudit.TermAdd));
			Assert.Equal((AuditActorKinds.User, (long?)chief.ID, TextRuleAudit.TargetKind), (entry.ActorKind, entry.ActorID, entry.TargetKind));
			Assert.Contains("zorg blat", entry.Details);

			// this copy applies a change at once
			Assert.Equal(((Failure?)null, "you **** **** gg"), TextChecks.ChatContent("you zorg blat gg"));

			Assert.Equal(409, (await admin.SendJson("POST", TermsPath, new { term = "zorg blat", language = "en", category = "insult", severity = "flag", match = "word" }, csrf)).Status);
			PageResponse invalid = await admin.SendJson("POST", TermsPath, new { term = "x", language = "fr", category = "insult", severity = "flag", match = "word" }, csrf);
			Assert.Equal((400, "invalid_argument", "language"), (invalid.Status, AdminArea.ErrorOf(invalid), Json(invalid).GetProperty("params").GetProperty("field").GetString()));
			Assert.Equal(400, (await admin.SendJson("POST", TermsPath, new { term = " ", language = "en", category = "insult", severity = "flag", match = "word" }, csrf)).Status);
			Assert.Equal(400, (await admin.SendJson("POST", TermsPath, new { term = "x", language = "en", category = "rude", severity = "flag", match = "word" }, csrf)).Status);
			Assert.Single(await AuditOf(probe, TextRuleAudit.TermAdd));

			PageResponse updated = await admin.Patch($"{TermsPath}/{id}", new { severity = "block" }, csrf);
			Assert.Equal((200, "block", "insult"), (updated.Status, Json(updated).GetProperty("severity").GetString(), Json(updated).GetProperty("category").GetString()));
			Assert.Equal(TextChecks.ChatBlocked, TextChecks.ChatContent("you zorg blat gg").Blocked?.Code);
			Assert.Contains("\"from\"", Assert.Single(await AuditOf(probe, TextRuleAudit.TermUpdate)).Details);
			Assert.Equal(404, (await admin.Patch($"{TermsPath}/999999", new { severity = "block" }, csrf)).Status);

			// without the antiforgery header nothing changes
			Assert.Equal(400, (await admin.SendJson("DELETE", $"{TermsPath}/{id}", new { }, null)).Status);
			Assert.Equal(TextChecks.ChatBlocked, TextChecks.ChatContent("you zorg blat gg").Blocked?.Code);
			Assert.Equal(204, (await admin.SendJson("DELETE", $"{TermsPath}/{id}", new { }, csrf)).Status);
			Assert.Single(await AuditOf(probe, TextRuleAudit.TermRemove));
			Assert.Equal("you zorg blat gg", TextChecks.ChatContent("you zorg blat gg").Content);

			Assert.Equal("fuck", ProfileActions.ReservedWordIn("Fuckingham"));
			PageResponse exception = await admin.SendJson("POST", ExceptionsPath, new { term = "Fuckingham", language = "en" }, csrf);
			Assert.Equal(201, exception.Status);
			Assert.Null(ProfileActions.ReservedWordIn("Fuckingham"));
			Assert.Contains(Json(await admin.Get(ExceptionsPath)).GetProperty("exceptions").EnumerateArray(), e => e.GetProperty("term").GetString() == "fuckingham");
			Assert.Equal(204, (await admin.SendJson("DELETE", $"{ExceptionsPath}/{Json(exception).GetProperty("id").GetInt64()}", new { }, csrf)).Status);
			Assert.Equal("fuck", ProfileActions.ReservedWordIn("Fuckingham"));
			Assert.Equal((1, 1), ((await AuditOf(probe, TextRuleAudit.ExceptionAdd)).Count, (await AuditOf(probe, TextRuleAudit.ExceptionRemove)).Count));
		}
		finally
		{
			TextChecks.UseDefaults();
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffEditTheRulesAfterValidationAndTestAText(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		try
		{
			await SeedAsync(probe);
			(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();

			JsonElement workflows = Json(await admin.Get(WorkflowsPath)).GetProperty("workflows");
			Assert.Equal(TextKinds.All, workflows.EnumerateArray().Select(w => w.GetProperty("kind").GetString()!).ToArray());
			JsonElement chat = workflows.EnumerateArray().Single(w => w.GetProperty("kind").GetString() == "chat");
			Assert.Equal(["allow", "block", "mask"], chat.GetProperty("outcomes").EnumerateArray().Select(o => o.GetString()!).ToArray());
			Assert.Equal("input.Block > 0", chat.GetProperty("definition").GetProperty("rules")[0].GetProperty("expression").GetString());

			// a rule that does not compile, reaches past the input or decides an outcome its kind has not is refused, nothing changes
			PageResponse bad = await admin.SendJson("PUT", WorkflowsPath + "/chat", new { rules = new[] { new { name = "x", expression = "input.Block >", outcome = "block", enabled = true } } }, csrf);
			Assert.Equal((400, "invalid_argument", "rules"), (bad.Status, AdminArea.ErrorOf(bad), Json(bad).GetProperty("params").GetProperty("field").GetString()));
			Assert.StartsWith("rule x", Json(bad).GetProperty("params").GetProperty("reason").GetString());
			Assert.Equal(400, (await admin.SendJson("PUT", WorkflowsPath + "/chat", new { rules = new[] { new { name = "x", expression = "System.Environment.Exit(1) == null", outcome = "block", enabled = true } } }, csrf)).Status);
			Assert.Equal(400, (await admin.SendJson("PUT", WorkflowsPath + "/lobby_name", new { rules = new[] { new { name = "x", expression = "input.Block > 0", outcome = "mask", enabled = true } } }, csrf)).Status);
			Assert.Equal(400, (await admin.SendJson("PUT", WorkflowsPath + "/chat", new { rules = Array.Empty<object>(), actions = "x" }, csrf)).Status);
			Assert.Equal(404, (await admin.SendJson("PUT", WorkflowsPath + "/profile", new { rules = Array.Empty<object>() }, csrf)).Status);
			Assert.Empty(await AuditOf(probe, TextRuleAudit.WorkflowUpdate));
			Assert.Equal(TextChecks.ChatBlocked, TextChecks.ChatContent("fuck this").Blocked?.Code);

			PageResponse saved = await admin.SendJson("PUT", WorkflowsPath + "/chat", new { rules = new[] { new { name = "mask all", expression = "input.Matches > 0", outcome = "mask", enabled = true } } }, csrf);
			Assert.Equal(200, saved.Status);
			Assert.Equal("mask all", Json(saved).GetProperty("definition").GetProperty("rules")[0].GetProperty("name").GetString());
			Assert.Equal(((Failure?)null, "**** this"), TextChecks.ChatContent("fuck this"));
			Assert.Contains("mask all", Assert.Single(await AuditOf(probe, TextRuleAudit.WorkflowUpdate)).Details);

			PageResponse tested = await admin.SendJson("POST", TestPath, new { kind = "chat", text = "sh1t m3ras" }, csrf);
			Assert.Equal(200, tested.Status);
			JsonElement result = Json(tested);
			Assert.Equal(("mask", "mask all", "**** *****"), (result.GetProperty("outcome").GetString(), result.GetProperty("rule").GetString(), result.GetProperty("masked").GetString()));
			Assert.Contains(result.GetProperty("readings").EnumerateArray(), r => r.GetProperty("name").GetString() == "folded" && r.GetProperty("text").GetString() == "shit meras");
			Assert.Equal(["sh1t", "m3ras"], result.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("text").GetString()!).ToArray());
			Assert.Equal(2, result.GetProperty("facts").GetProperty("matches").GetInt32());
			Assert.Equal(400, (await admin.SendJson("POST", TestPath, new { kind = "motd", text = "x" }, csrf)).Status);
		}
		finally
		{
			TextChecks.UseDefaults();
		}
	}
}
