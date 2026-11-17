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
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The reads behind the moderator views of the staff pages: a player's reports and who closed a report
[Collection(AdminAreaCollection.Name)]
public class ModeratorViewsTests
{
	private static readonly DateTime s_now = DateTime.UtcNow;

	private static async Task<User> AddUser(ProbeDatabase probe, string name, int tag)
	{
		await using AppDbContext db = probe.Open();
		var user = new User { Username = name, UsernameNormalized = NameNormalizer.Normalize(name), NameTag = tag, CreatedAt = s_now, UpdatedAt = s_now };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}

	private static JsonElement[] Rows(PageResponse response, string list)
	{
		Assert.True(response.Status == 200, response.Status + ": " + response.Body);
		return JsonDocument.Parse(response.Body).RootElement.GetProperty(list).EnumerateArray().ToArray();
	}

	private static string FieldOf(PageResponse response)
	{
		Assert.Equal(400, response.Status);
		JsonElement root = JsonDocument.Parse(response.Body).RootElement;
		Assert.Equal("invalid_argument", root.GetProperty("error").GetString());
		return root.GetProperty("params").GetProperty("field").GetString()!;
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ReportsOfOnePlayerAndWhoClosedThem(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		User warden = await AdminArea.Grant(probe, "warden@example.org", StaffRoles.Moderator);
		User alpha = await AddUser(probe, "Alpha", 1);
		User bravo = await AddUser(probe, "Bravo", 2);
		long closed;
		await using (AppDbContext db = probe.Open())
		{
			db.PlayerReports.AddRange(
				new PlayerReport { ReporterID = alpha.ID, ReportedUserID = bravo.ID, Reason = "rude", Evidence = "{\"messages\":[]}", CreatedAt = s_now.AddHours(-2) },
				new PlayerReport { ReporterID = alpha.ID, ReportedUserID = bravo.ID, Reason = "again", Evidence = "{\"messages\":[]}", CreatedAt = s_now.AddHours(-1) },
				new PlayerReport { ReporterID = bravo.ID, ReportedUserID = alpha.ID, Reason = "back", Evidence = "{\"messages\":[]}", CreatedAt = s_now });
			await db.SaveChangesAsync();
			closed = db.PlayerReports.Single(r => r.Reason == "rude").ID;
			Assert.True(await Chat.ResolveReport(db, closed, PlayerReportStatuses.Actioned, "warned", AuditActorKinds.User, warden.ID));
		}

		JsonElement[] against = Rows(await moderator.Get($"/api/v1/reports/search?status=all&reported_id={bravo.ID}"), "reports");
		Assert.Equal(["rude", "again"], against.Select(r => r.GetProperty("reason").GetString()));
		JsonElement[] filed = Rows(await moderator.Get($"/api/v1/reports/search?status=all&reporter_id={bravo.ID}"), "reports");
		Assert.Equal("back", Assert.Single(filed).GetProperty("reason").GetString());

		JsonElement done = against[0];
		Assert.Equal("user", done.GetProperty("resolved_by_kind").GetString());
		Assert.Equal(warden.ID, done.GetProperty("resolved_by").GetProperty("id").GetInt64());
		Assert.Equal("Warden", done.GetProperty("resolved_by").GetProperty("name").GetString());
		Assert.Equal("warned", done.GetProperty("resolution_note").GetString());
		Assert.Equal(JsonValueKind.Null, against[1].GetProperty("resolved_by").ValueKind);
		Assert.Equal(JsonValueKind.Null, against[1].GetProperty("resolved_by_kind").ValueKind);

		// the open queue of before is unchanged: no player argument lists every open report
		Assert.Equal(2, Rows(await moderator.Get("/api/v1/reports/search"), "reports").Length);

		Assert.Equal("reported_id", FieldOf(await moderator.Get("/api/v1/reports/search?reported_id=abc")));
		Assert.Equal("reported_id", FieldOf(await moderator.Get("/api/v1/reports/search?reported_id=0")));
		Assert.Equal("reporter_id", FieldOf(await moderator.Get("/api/v1/reports/search?reporter_id=-3")));
	}
}
