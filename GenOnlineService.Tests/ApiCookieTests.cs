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

using GenOnlineService.Api;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The sign-in cookie on /api/v1, GitLab style (T16): the routes marked [AllowSignInCookie] take the cookie as well as a
// bearer token; a cookie change needs this user's antiforgery header token, a foreign Origin is refused, every other
// route still answers a cookie alone with 401, and bearer callers are unchanged
[Collection(AdminAreaCollection.Name)]
public class ApiCookieTests
{
	private static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role, bool twoFactor = true)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role, twoFactor));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ACookieReadOnAnOptedInRouteWorksForStaff(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, User warden) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);

		PageResponse search = await moderator.Get("/api/v1/players/search?query=Warden");
		Assert.Equal(200, search.Status);
		Assert.Equal(warden.ID, JsonDocument.Parse(search.Body).RootElement.GetProperty("players")[0].GetProperty("id").GetInt64());

		PageResponse player = await moderator.Get($"/api/v1/players/{warden.ID}/staff_view");
		Assert.Equal(200, player.Status);
		Assert.Equal("Warden", JsonDocument.Parse(player.Body).RootElement.GetProperty("name").GetString());

		Assert.Equal(200, (await moderator.Get("/api/v1/reports/search?status=open,dismissed&sort=desc")).Status);

		// A1: staff read a match through the cookie as through the first staff area, here one that does not exist
		PageResponse match = await moderator.Get("/api/v1/matches/424242");
		Assert.Equal(404, match.Status);
		Assert.Equal("not_found", AdminArea.ErrorOf(match));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ACookieChangeNeedsThisUsersHeaderToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		(AdminBrowser other, _) = await StaffAsync(probe, "Other", StaffRoles.Moderator);
		var player = new AdminBrowser(probe);
		await player.SignUp("Plain", "plain@example.org");
		User plain = (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!;
		var rename = new { name = "Cleaned", reason = "probe" };
		// the rank rule reads the process-wide role cache
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}

		PageResponse missing = await moderator.Patch($"/api/v1/players/{plain.ID}", rename);
		Assert.Equal(400, missing.Status);
		Assert.Equal(ApiAuthentication.CsrfError, AdminArea.ErrorOf(missing));

		PageResponse stolen = await moderator.Patch($"/api/v1/players/{plain.ID}", rename, await other.Csrf());
		Assert.Equal(400, stolen.Status);
		Assert.Equal(ApiAuthentication.CsrfError, AdminArea.ErrorOf(stolen));
		Assert.Equal("Plain", (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!.Username);

		Assert.Equal(204, (await moderator.Patch($"/api/v1/players/{plain.ID}", rename, await moderator.Csrf())).Status);
		Assert.Equal("Cleaned", (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!.Username);
	}

	// routes not marked keep their bearer-only answer to a cookie alone
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ACookieOnARouteNotOptedInIsUnauthorizedAsBefore(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);

		foreach (string path in new[] { "/api/v1/reports", $"/api/v1/players/{chief.ID}/sanctions", "/api/v1/me", "/api/v1/matches" })
		{
			PageResponse response = await admin.Get(path);
			Assert.Equal(401, response.Status);
			Assert.Equal("unauthorized", AdminArea.ErrorOf(response));
		}
		PageResponse reset = await admin.SendJson("POST", $"/api/v1/players/{chief.ID}/reset_name", new { reason = "probe" }, await admin.Csrf());
		Assert.Equal(401, reset.Status);
	}

	// an API token needs no antiforgery header, with or without a sign-in cookie next to it
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ABearerChangeNeedsNoHeaderAsBefore(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		string token = ApiTokenFormat.Generate();
		await using (AppDbContext db = probe.Open())
		{
			var account = new ServiceAccount { Name = "reviewer", CreatedBy = "test", CreatedAt = DateTime.UtcNow };
			db.ServiceAccounts.Add(account);
			await db.SaveChangesAsync();
			db.ServiceAccountPermissions.Add(new ServiceAccountPermission { ServiceAccountID = account.ID, Permission = Permissions.AnticheatReview, GrantedBy = "test", GrantedAt = DateTime.UtcNow });
			db.ApiTokens.Add(new ApiToken
			{
				OwnerServiceID = account.ID,
				Name = "reviewer",
				TokenHash = ApiTokenFormat.Hash(token),
				TokenLast4 = ApiTokenFormat.Last4(token),
				Scopes = Permissions.AnticheatReview,
				CreatedBy = "test",
				CreatedAt = DateTime.UtcNow,
				ExpiresAt = DateTime.UtcNow.AddDays(1)
			});
			await db.SaveChangesAsync();
			await ApiTokenCache.Reload(db);
		}
		var bearer = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };

		var anonymous = new AdminBrowser(probe);
		Assert.Equal(200, (await anonymous.Get("/api/v1/anticheat_reports/modules", bearer)).Status);
		PageResponse review = await anonymous.Patch("/api/v1/anticheat_reports/modules/999", new { reviewed = true }, bearer);
		Assert.Equal(404, review.Status);
		Assert.Equal("not_found", AdminArea.ErrorOf(review));

		// a signed-in browser sending a bearer token is a bearer call: no header needed, the token's permissions count
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		Assert.Equal(404, (await moderator.Patch("/api/v1/anticheat_reports/modules/999", new { reviewed = true }, bearer)).Status);
		Assert.Equal(401, (await moderator.Get("/api/v1/anticheat_reports/modules", new() { ["Authorization"] = "Bearer gopat-unknown" })).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ACookieFromAForeignOriginIsForbidden(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);

		PageResponse foreign = await moderator.Get("/api/v1/players/search?query=Warden", new() { ["Origin"] = "https://evil.example" });
		Assert.Equal(403, foreign.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(foreign));
		Assert.Equal(403, (await moderator.Get("/api/v1/players/search?query=Warden", new() { ["Origin"] = "http://other." + AdminBrowser.Host })).Status);
		Assert.Equal(200, (await moderator.Get("/api/v1/players/search?query=Warden", new() { ["Origin"] = "http://" + AdminBrowser.Host })).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task APlayersCookieIsForbidden(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var player = new AdminBrowser(probe);
		await player.SignUp("Plain", "plain@example.org");
		User plain = (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!;

		foreach (string path in new[] { "/api/v1/players/search?query=Plain", $"/api/v1/players/{plain.ID}/staff_view", "/api/v1/reports/search", "/api/v1/matches/1" })
		{
			PageResponse response = await player.Get(path);
			Assert.Equal(403, response.Status);
			Assert.Equal("forbidden", AdminArea.ErrorOf(response));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PasswordStaffWithoutTwoFactorAreRefused(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator, twoFactor: false);

		PageResponse response = await moderator.Get("/api/v1/players/search?query=Warden");
		Assert.Equal(403, response.Status);
		Assert.Equal(ApiAuthentication.TwoFactorRequired, AdminArea.ErrorOf(response));
	}
}
