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

using GenOnlineService.Catalog;
using GenOnlineService.Flags;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace GenOnlineService.Tests;

// The game config page (S10): the flags routes take the sign-in cookie of a flags.manage holder under the cookie rules,
// audit every draft edit, publish and rollback, and a publish or rollback leaves the game the answer the revision holds
[Collection(AdminAreaCollection.Name)]
public class FlagsCookieTests
{
	private const string TimeoutKey = "client.connection.reconnect_timeout_ms";

	private static async Task<AdminBrowser> StaffAsync(ProbeDatabase probe, string name, string role)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		await AdminArea.Grant(probe, email, role);
		return browser;
	}

	private static JsonNode Json(PageResponse response) => JsonNode.Parse(response.Body)!;

	private static string ETagOf(PageResponse response) => response.Headers!["ETag"].ToString();

	private static async Task<List<string>> AuditActions(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		return await db.AuditLog.AsNoTracking().Where(e => e.TargetKind == AuditTargetKinds.Flag || e.TargetKind == AuditTargetKinds.FlagRevision).OrderBy(e => e.ID).Select(e => e.Action).ToListAsync();
	}

	private static async Task<JsonObject> ServedConfig(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		await FlagCatalog.Reload(db);
		return FlagCatalog.Current.ServiceConfigFor(FlagContext.Create(7, [], "c", null, "NL", "test"));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AStaffCookieEditsPublishesAndRollsBackTheFlagsAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using (AppDbContext db = probe.Open())
		{
			await FlagSeed.EnsureAsync(db);
		}
		CatalogSignals.Register(CatalogResources.Flags, FlagCatalog.Reload);
		try
		{
			AdminBrowser admin = await StaffAsync(probe, "Chief", StaffRoles.Admin);
			Dictionary<string, string> csrf = await admin.Csrf();
			JsonObject before = await ServedConfig(probe);
			int seeded = (await AuditActions(probe)).Count;

			PageResponse list = await admin.Get("/api/v1/flags");
			Assert.Equal(200, list.Status);
			Assert.Equal(1, Json(list)["revision"]!.GetValue<int>());
			Assert.Equal(38, Json(list)["flags"]!.AsArray().Count);

			// publishing the unchanged draft is refused and the game keeps the same answer
			PageResponse nothing = await admin.SendJson("POST", "/api/v1/flags/draft/publish", new { comment = "nothing" }, new(csrf) { ["If-Match"] = Json(list)["draft_etag"]!.GetValue<string>() });
			Assert.Equal(FlagCodes.NoChanges, AdminArea.ErrorOf(nothing));
			Assert.True(JsonNode.DeepEquals(before, await ServedConfig(probe)));

			PageResponse one = await admin.Get("/api/v1/flags/" + TimeoutKey);
			Assert.Equal(200, one.Status);
			JsonObject flag = Json(one)["flag"]!.AsObject();
			flag["value"] = 45000;

			PageResponse edited = await admin.SendJson("PUT", "/api/v1/flags/" + TimeoutKey, flag, new(csrf) { ["If-Match"] = ETagOf(one) });
			Assert.Equal(200, edited.Status);
			Assert.Equal("changed", Json(edited)["status"]!.GetValue<string>());

			PageResponse changes = await admin.Get("/api/v1/flags/draft/changes");
			Assert.Equal(200, changes.Status);
			Assert.Contains(TimeoutKey, changes.Body);

			PageResponse draft = await admin.Get("/api/v1/flags");
			PageResponse published = await admin.SendJson("POST", "/api/v1/flags/draft/publish", new { comment = "longer timeout" }, new(csrf) { ["If-Match"] = Json(draft)["draft_etag"]!.GetValue<string>() });
			Assert.Equal(201, published.Status);
			Assert.Equal(2, Json(published)["number"]!.GetValue<int>());
			Assert.Equal(45000, (await ServedConfig(probe))["connection"]!["reconnect_timeout_ms"]!.GetValue<int>());

			PageResponse history = await admin.Get("/api/v1/flags/revisions");
			Assert.Equal(200, history.Status);
			Assert.Equal([2, 1], Json(history).AsArray().Select(r => r!["number"]!.GetValue<int>()));

			PageResponse rolled = await admin.SendJson("POST", "/api/v1/flags/revisions/1/rollback", new { comment = "back to the seed" }, csrf);
			Assert.Equal(201, rolled.Status);
			Assert.Equal(1, Json(rolled)["rollback_of"]!.GetValue<int>());
			Assert.True(JsonNode.DeepEquals(before, await ServedConfig(probe)));

			Assert.Equal([FlagActions.Update, FlagActions.Publish, FlagActions.Rollback], (await AuditActions(probe)).Skip(seeded).ToList());
		}
		finally
		{
			FlagCatalog.Install(FlagSnapshot.Empty);
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheFlagsRoutesRefuseStaffWithoutTheRightOrTheHeaderToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		await using (AppDbContext db = probe.Open())
		{
			await FlagSeed.EnsureAsync(db);
		}
		AdminBrowser admin = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		AdminBrowser moderator = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		Dictionary<string, string> moderatorCsrf = await moderator.Csrf();
		Dictionary<string, string> csrf = await admin.Csrf();
		int seeded = (await AuditActions(probe)).Count;

		// a moderator holds no flags.manage
		foreach (PageResponse refused in new[]
		{
			await moderator.Get("/api/v1/flags"),
			await moderator.Get("/api/v1/flags/revisions"),
			await moderator.SendJson("POST", "/api/v1/flags/draft/publish", new { }, moderatorCsrf)
		})
		{
			Assert.Equal(403, refused.Status);
			Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
		}

		// a cookie change without the header token, or from another origin, never reaches the route
		PageResponse one = await admin.Get("/api/v1/flags/" + TimeoutKey);
		JsonObject flag = Json(one)["flag"]!.AsObject();
		flag["value"] = 45000;
		foreach (PageResponse refused in new[]
		{
			await admin.SendJson("PUT", "/api/v1/flags/" + TimeoutKey, flag),
			await admin.SendJson("POST", "/api/v1/flags/draft/publish", new { }),
			await admin.SendJson("POST", "/api/v1/flags/revisions/1/rollback", new { }),
			await admin.SendJson("POST", "/api/v1/flags/draft/import", new { flags = new { } })
		})
		{
			Assert.Equal(400, refused.Status);
			Assert.Equal("csrf", AdminArea.ErrorOf(refused));
		}
		Dictionary<string, string> foreign = new(csrf) { ["Origin"] = "https://evil.example" };
		Assert.Equal(403, (await admin.SendJson("PUT", "/api/v1/flags/" + TimeoutKey, flag, foreign)).Status);

		await using AppDbContext check = probe.Open();
		Assert.Equal(30000, (await FlagStore.GetDraft(check, TimeoutKey))!.Flag.Value.GetInt32());
		Assert.Equal(1, await check.FlagRevisions.CountAsync());
		Assert.Equal(seeded, (await AuditActions(probe)).Count);
	}
}
