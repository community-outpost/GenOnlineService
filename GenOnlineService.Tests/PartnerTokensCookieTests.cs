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

using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// Partner service accounts and their gopat- tokens from the staff pages (S1): the service account routes take the sign-in
// cookie of a tokens.manage holder under the cookie rules, keep the shared token rules, and audit every change; service
// account tokens are rotated and revoked over REST too. Bearer web sessions keep working as before
[Collection(AdminAreaCollection.Name)]
public class PartnerTokensCookieTests
{
	private static readonly object s_empty = new { };

	private static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role, params (string Key, string Value)[] extra)
	{
		var browser = new AdminBrowser(probe, extra);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role));
	}

	private static string InDays(int days) => DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	private static JsonElement Json(PageResponse response) => JsonDocument.Parse(response.Body).RootElement;

	private static Dictionary<string, string> Bearer(string token) => new() { ["Authorization"] = "Bearer " + token };

	// A route the partner's match_history.read token may call: 404 for a match that does not exist, 401 for a dead token
	private static async Task<int> UseToken(ProbeDatabase probe, string token) => (await new AdminBrowser(probe).Get("/api/v1/matches/999999", Bearer(token))).Status;

	private static async Task<List<AuditLogEntry>> AuditOf(ProbeDatabase probe, string targetKind)
	{
		await using AppDbContext db = probe.Open();
		return await db.AuditLog.AsNoTracking().Where(e => e.TargetKind == targetKind).OrderBy(e => e.ID).ToListAsync();
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AStaffCookieManagesAPartnerAndItsTokensAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();

		PageResponse created = await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "partner", description = "A partner site" }, csrf);
		Assert.Equal(201, created.Status);
		long id = Json(created).GetProperty("id").GetInt64();
		Assert.Equal("A partner site", Json(created).GetProperty("description").GetString());

		PageResponse granted = await admin.Patch($"/api/v1/service_accounts/{id}", new { grants = new { add = new[] { "match_history.read", "lobbies.read" } } }, csrf);
		Assert.Equal(200, granted.Status);
		Assert.Equal(["lobbies.read", "match_history.read"], Json(granted).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

		PageResponse list = await admin.Get("/api/v1/service_accounts");
		Assert.Equal(200, list.Status);
		Assert.Equal("partner", Assert.Single(Json(list).GetProperty("service_accounts").EnumerateArray()).GetProperty("name").GetString());
		PageResponse one = await admin.Get($"/api/v1/service_accounts/{id}");
		Assert.Equal(200, one.Status);
		Assert.Equal(("partner", 2), (Json(one).GetProperty("name").GetString(), Json(one).GetProperty("permissions").GetArrayLength()));
		Assert.Equal(404, (await admin.Get("/api/v1/service_accounts/999999")).Status);

		PageResponse minted = await admin.SendJson("POST", $"/api/v1/service_accounts/{id}/personal_access_tokens",
			new { name = "partner-prod", scopes = new[] { "match_history.read" }, expires_at = InDays(90) }, csrf);
		Assert.Equal(201, minted.Status);
		string first = Json(minted).GetProperty("token").GetString()!;
		long firstID = Json(minted).GetProperty("id").GetInt64();
		Assert.StartsWith("gopat-", first);
		Assert.Equal(404, await UseToken(probe, first));

		// the secret is in the create and rotate answers only, never in a list
		PageResponse tokens = await admin.Get($"/api/v1/service_accounts/{id}/personal_access_tokens");
		Assert.Equal(200, tokens.Status);
		Assert.DoesNotContain(first, tokens.Body);
		Assert.False(Assert.Single(Json(tokens).GetProperty("personal_access_tokens").EnumerateArray()).TryGetProperty("token", out _));

		PageResponse rotated = await admin.SendJson("POST", $"/api/v1/service_accounts/{id}/personal_access_tokens/{firstID}/rotate", s_empty, csrf);
		Assert.Equal(201, rotated.Status);
		string second = Json(rotated).GetProperty("token").GetString()!;
		long secondID = Json(rotated).GetProperty("id").GetInt64();
		Assert.NotEqual(first, second);
		Assert.Equal(401, await UseToken(probe, first));
		Assert.Equal(404, await UseToken(probe, second));
		PageResponse afterRotate = await admin.Get($"/api/v1/service_accounts/{id}/personal_access_tokens");
		Assert.DoesNotContain(second, afterRotate.Body);
		Assert.Equal(2, Json(afterRotate).GetProperty("personal_access_tokens").GetArrayLength());

		Assert.Equal(204, (await admin.SendJson("DELETE", $"/api/v1/service_accounts/{id}/personal_access_tokens/{secondID}", s_empty, csrf)).Status);
		Assert.Equal(401, await UseToken(probe, second));
		await using (AppDbContext db = probe.Open())
		{
			ApiToken revoked = await db.ApiTokens.AsNoTracking().SingleAsync(t => t.ID == secondID);
			Assert.NotNull(revoked.RevokedAt);
			Assert.Equal($"user:{chief.ID}", revoked.RevokedBy);
		}

		PageResponse disabled = await admin.Patch($"/api/v1/service_accounts/{id}", new { disabled = true, grants = new { remove = new[] { "lobbies.read" } } }, csrf);
		Assert.Equal(200, disabled.Status);
		Assert.NotEqual(JsonValueKind.Null, Json(disabled).GetProperty("disabled_at").ValueKind);
		Assert.Equal(200, (await admin.Patch($"/api/v1/service_accounts/{id}", new { disabled = false }, csrf)).Status);

		List<AuditLogEntry> accountAudit = await AuditOf(probe, AuditTargetKinds.ServiceAccount);
		Assert.Equal(["sa_create", "sa_grant", "sa_grant", "sa_revoke", "sa_disable", "sa_enable"], accountAudit.Select(e => e.Action));
		List<AuditLogEntry> tokenAudit = await AuditOf(probe, AuditTargetKinds.Token);
		Assert.Equal(["token_mint", "token_rotate", "token_revoke"], tokenAudit.Select(e => e.Action));
		Assert.Equal([firstID, secondID, secondID], tokenAudit.Select(e => e.TargetID!.Value));
		foreach (AuditLogEntry entry in accountAudit.Concat(tokenAudit))
		{
			Assert.Equal((AuditActorKinds.User, (long?)chief.ID, $"user:{chief.ID}", ModerationSources.Api), (entry.ActorKind, entry.ActorID, entry.Actor, entry.Source));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task WithoutTokensManageOrTheHeaderTheCookieChangesNothing(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		long id;
		long tokenID;
		Dictionary<string, string> csrf = await admin.Csrf();
		id = Json(await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "partner" }, csrf)).GetProperty("id").GetInt64();
		Assert.Equal(200, (await admin.Patch($"/api/v1/service_accounts/{id}", new { grants = new { add = new[] { "match_history.read" } } }, csrf)).Status);
		PageResponse minted = await admin.SendJson("POST", $"/api/v1/service_accounts/{id}/personal_access_tokens", new { name = "prod", scopes = new[] { "match_history.read" }, expires_at = InDays(30) }, csrf);
		tokenID = Json(minted).GetProperty("id").GetInt64();
		int auditBefore = (await AuditOf(probe, AuditTargetKinds.ServiceAccount)).Count + (await AuditOf(probe, AuditTargetKinds.Token)).Count;

		// a moderator holds no tokens.manage
		Dictionary<string, string> moderatorCsrf = await moderator.Csrf();
		foreach (PageResponse refused in new[]
		{
			await moderator.Get("/api/v1/service_accounts"),
			await moderator.Get($"/api/v1/service_accounts/{id}"),
			await moderator.Get($"/api/v1/service_accounts/{id}/personal_access_tokens"),
			await moderator.SendJson("POST", "/api/v1/service_accounts", new { name = "other" }, moderatorCsrf),
			await moderator.Patch($"/api/v1/service_accounts/{id}", new { disabled = true }, moderatorCsrf),
			await moderator.SendJson("POST", $"/api/v1/service_accounts/{id}/personal_access_tokens", new { name = "x", scopes = new[] { "match_history.read" }, expires_at = InDays(30) }, moderatorCsrf),
			await moderator.SendJson("POST", $"/api/v1/service_accounts/{id}/personal_access_tokens/{tokenID}/rotate", s_empty, moderatorCsrf),
			await moderator.SendJson("DELETE", $"/api/v1/service_accounts/{id}/personal_access_tokens/{tokenID}", s_empty, moderatorCsrf)
		})
		{
			Assert.Equal(403, refused.Status);
			Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
		}

		// a cookie change without the header token, or from another origin, never reaches the route
		foreach (PageResponse refused in new[]
		{
			await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "other" }),
			await admin.Patch($"/api/v1/service_accounts/{id}", new { disabled = true }),
			await admin.SendJson("POST", $"/api/v1/service_accounts/{id}/personal_access_tokens", new { name = "x", scopes = new[] { "match_history.read" }, expires_at = InDays(30) }),
			await admin.SendJson("POST", $"/api/v1/service_accounts/{id}/personal_access_tokens/{tokenID}/rotate", s_empty),
			await admin.SendJson("DELETE", $"/api/v1/service_accounts/{id}/personal_access_tokens/{tokenID}", s_empty)
		})
		{
			Assert.Equal(400, refused.Status);
			Assert.Equal("csrf", AdminArea.ErrorOf(refused));
		}
		Dictionary<string, string> foreign = new(csrf) { ["Origin"] = "https://evil.example" };
		Assert.Equal(403, (await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "other" }, foreign)).Status);

		await using AppDbContext db = probe.Open();
		Assert.Equal(["partner"], await db.ServiceAccounts.Select(a => a.Name).ToListAsync());
		Assert.Null((await db.ServiceAccounts.SingleAsync()).DisabledAt);
		Assert.Equal(1, await db.ApiTokens.CountAsync());
		Assert.Null((await db.ApiTokens.SingleAsync()).RevokedAt);
		Assert.Equal(auditBefore, (await AuditOf(probe, AuditTargetKinds.ServiceAccount)).Count + (await AuditOf(probe, AuditTargetKinds.Token)).Count);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PrivilegedPermissionsStayCliOnlyOverTheCookie(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();
		long id = Json(await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "partner" }, csrf)).GetProperty("id").GetInt64();

		foreach (string privileged in new[] { Permissions.UsersRevoke, Permissions.OpsDatabase })
		{
			PageResponse grant = await admin.Patch($"/api/v1/service_accounts/{id}", new { grants = new { add = new[] { privileged } } }, csrf);
			Assert.Equal(403, grant.Status);
			Assert.Equal(privileged, Json(grant).GetProperty("params").GetProperty("permission").GetString());
		}

		// granted on the host, a privileged permission still never goes on a token minted over REST
		await using (AppDbContext db = probe.Open())
		{
			db.ServiceAccountPermissions.Add(new ServiceAccountPermission { ServiceAccountID = id, Permission = Permissions.UsersRevoke, GrantedBy = "cli:test", GrantedAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
		}
		PageResponse mint = await admin.SendJson("POST", $"/api/v1/service_accounts/{id}/personal_access_tokens",
			new { name = "prod", scopes = new[] { Permissions.UsersRevoke }, expires_at = InDays(10), allowed_ips = new[] { "203.0.113.4/32" } }, csrf);
		Assert.Equal(403, mint.Status);
		Assert.Equal(Permissions.UsersRevoke, Json(mint).GetProperty("params").GetProperty("permission").GetString());

		await using AppDbContext check = probe.Open();
		Assert.Equal([Permissions.UsersRevoke], await check.ServiceAccountPermissions.Select(p => p.Permission).ToListAsync());
		Assert.Equal(0, await check.ApiTokens.CountAsync());
		Assert.DoesNotContain(await check.AuditLog.Select(e => e.Action).ToListAsync(), a => a == "sa_grant" || a == "token_mint");
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnotherOwnersTokenIsNotFoundUnderAServiceAccount(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();
		long a = Json(await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "alpha" }, csrf)).GetProperty("id").GetInt64();
		long b = Json(await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "beta" }, csrf)).GetProperty("id").GetInt64();
		Assert.Equal(200, (await admin.Patch($"/api/v1/service_accounts/{a}", new { grants = new { add = new[] { "lobbies.read" } } }, csrf)).Status);
		string tokens = $"/api/v1/service_accounts/{a}/personal_access_tokens";

		// the expiry is required and at most ApiTokens:max_lifetime_days away; the allowed addresses are kept
		foreach (object refused in new object[] { new { name = "prod", scopes = new[] { "lobbies.read" } }, new { name = "prod", scopes = new[] { "lobbies.read" }, expires_at = InDays(400) } })
		{
			PageResponse expiry = await admin.SendJson("POST", tokens, refused, csrf);
			Assert.Equal(400, expiry.Status);
			Assert.Equal("expires_at", Json(expiry).GetProperty("params").GetProperty("field").GetString());
		}
		long tokenID = Json(await admin.SendJson("POST", tokens, new { name = "prod", scopes = new[] { "lobbies.read" }, expires_at = InDays(30), allowed_ips = new[] { "203.0.113.4/32" } }, csrf)).GetProperty("id").GetInt64();
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal("203.0.113.4/32", (await db.ApiTokens.AsNoTracking().SingleAsync(t => t.ID == tokenID)).AllowedIPs);
		}

		PageResponse rotate = await admin.SendJson("POST", $"/api/v1/service_accounts/{b}/personal_access_tokens/{tokenID}/rotate", s_empty, csrf);
		Assert.Equal(404, rotate.Status);
		Assert.Equal("token", Json(rotate).GetProperty("params").GetProperty("what").GetString());
		Assert.Equal(404, (await admin.SendJson("DELETE", $"/api/v1/service_accounts/{b}/personal_access_tokens/{tokenID}", s_empty, csrf)).Status);
		Assert.Equal(404, (await admin.SendJson("DELETE", $"/api/v1/service_accounts/999999/personal_access_tokens/{tokenID}", s_empty, csrf)).Status);

		// a revoked token cannot be rotated
		Assert.Equal(204, (await admin.SendJson("DELETE", $"/api/v1/service_accounts/{a}/personal_access_tokens/{tokenID}", s_empty, csrf)).Status);
		Assert.Equal(409, (await admin.SendJson("POST", $"/api/v1/service_accounts/{a}/personal_access_tokens/{tokenID}/rotate", s_empty, csrf)).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ADescriptionEditIsSavedAndAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();
		long id = Json(await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "partner", description = "Old text" }, csrf)).GetProperty("id").GetInt64();

		PageResponse edited = await admin.Patch($"/api/v1/service_accounts/{id}", new { description = "  Replay archive of the partner  " }, csrf);
		Assert.Equal(200, edited.Status);
		Assert.Equal("Replay archive of the partner", Json(edited).GetProperty("description").GetString());
		Assert.Equal("Replay archive of the partner", Json(await admin.Get($"/api/v1/service_accounts/{id}")).GetProperty("description").GetString());

		// an empty description clears it, a long one changes nothing
		Assert.Equal(JsonValueKind.Null, Json(await admin.Patch($"/api/v1/service_accounts/{id}", new { description = "" }, csrf)).GetProperty("description").ValueKind);
		PageResponse tooLong = await admin.Patch($"/api/v1/service_accounts/{id}", new { description = new string('d', 257) }, csrf);
		Assert.Equal(400, tooLong.Status);
		Assert.Equal("description", Json(tooLong).GetProperty("params").GetProperty("field").GetString());
		// a request without description leaves it as it is and writes no sa_update
		Assert.Equal(200, (await admin.Patch($"/api/v1/service_accounts/{id}", new { disabled = false }, csrf)).Status);

		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal("", (await db.ServiceAccounts.AsNoTracking().SingleAsync(a => a.ID == id)).Description);
		}
		List<AuditLogEntry> updates = (await AuditOf(probe, AuditTargetKinds.ServiceAccount)).Where(e => e.Action == "sa_update").ToList();
		Assert.Equal(2, updates.Count);
		JsonElement first = JsonDocument.Parse(updates[0].Details!).RootElement;
		Assert.Equal(("Old text", "Replay archive of the partner"), (first.GetProperty("old_description").GetString(), first.GetProperty("description").GetString()));
		Assert.Equal((AuditActorKinds.User, (long?)chief.ID, ModerationSources.Api), (updates[0].ActorKind, updates[0].ActorID, updates[0].Source));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TokensShowTheirAllowedAddressesAndAccountsTheirActiveTokens(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();
		long id = Json(await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "partner" }, csrf)).GetProperty("id").GetInt64();
		Assert.Equal(200, (await admin.Patch($"/api/v1/service_accounts/{id}", new { grants = new { add = new[] { "lobbies.read" } } }, csrf)).Status);
		string tokens = $"/api/v1/service_accounts/{id}/personal_access_tokens";

		PageResponse minted = await admin.SendJson("POST", tokens, new { name = "fenced", scopes = new[] { "lobbies.read" }, expires_at = InDays(30), allowed_ips = new[] { "203.0.113.4/32", "2001:db8::/48" } }, csrf);
		Assert.Equal(["203.0.113.4/32", "2001:db8::/48"], Json(minted).GetProperty("allowed_ips").EnumerateArray().Select(e => e.GetString()));
		long fenced = Json(minted).GetProperty("id").GetInt64();
		long open = Json(await admin.SendJson("POST", tokens, new { name = "open", scopes = new[] { "lobbies.read" }, expires_at = InDays(30) }, csrf)).GetProperty("id").GetInt64();
		long gone = Json(await admin.SendJson("POST", tokens, new { name = "gone", scopes = new[] { "lobbies.read" }, expires_at = InDays(30) }, csrf)).GetProperty("id").GetInt64();
		Assert.Equal(204, (await admin.SendJson("DELETE", $"{tokens}/{gone}", s_empty, csrf)).Status);

		PageResponse rotated = await admin.SendJson("POST", $"{tokens}/{fenced}/rotate?overlap_minutes=60", s_empty, csrf);
		Assert.Equal(201, rotated.Status);
		Assert.Equal(["203.0.113.4/32", "2001:db8::/48"], Json(rotated).GetProperty("allowed_ips").EnumerateArray().Select(e => e.GetString()));

		// the list carries the addresses, an empty list for a token any address may use, and never a secret
		List<JsonElement> listed = Json(await admin.Get(tokens)).GetProperty("personal_access_tokens").EnumerateArray().ToList();
		Assert.Equal(["203.0.113.4/32", "2001:db8::/48"], listed.Single(t => t.GetProperty("id").GetInt64() == fenced).GetProperty("allowed_ips").EnumerateArray().Select(e => e.GetString()));
		Assert.Empty(listed.Single(t => t.GetProperty("id").GetInt64() == open).GetProperty("allowed_ips").EnumerateArray());
		Assert.All(listed, t => Assert.False(t.TryGetProperty("token", out _)));

		DateTime used = new DateTime(2026, 11, 17, 10, 30, 0, DateTimeKind.Utc);
		await using (AppDbContext db = probe.Open())
		{
			await db.ApiTokens.Where(t => t.ID == open).ExecuteUpdateAsync(set => set.SetProperty(t => t.LastUsedAt, used));
			await db.ApiTokens.Where(t => t.ID == gone).ExecuteUpdateAsync(set => set.SetProperty(t => t.LastUsedAt, used.AddDays(-1)));
		}

		// active: the rotated token inside its overlap, its replacement and the open one; the revoked one is left out
		foreach (JsonElement account in new[] { Json(await admin.Get($"/api/v1/service_accounts/{id}")), Assert.Single(Json(await admin.Get("/api/v1/service_accounts")).GetProperty("service_accounts").EnumerateArray()) })
		{
			Assert.Equal(3, account.GetProperty("active_tokens").GetInt32());
			Assert.Equal(used, account.GetProperty("last_token_used_at").GetDateTime().ToUniversalTime());
		}
	}

	// previous behaviour: a gopat- token never manages tokens, even with tokens.manage, and the personal token routes
	// stay bearer only
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ATokenCallerIsRefusedAndPersonalTokenRoutesStayBearerOnly(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();
		long id = Json(await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "operator" }, csrf)).GetProperty("id").GetInt64();
		string token = ApiTokenFormat.Generate();
		await using (AppDbContext db = probe.Open())
		{
			db.ServiceAccountPermissions.Add(new ServiceAccountPermission { ServiceAccountID = id, Permission = Permissions.TokensManage, GrantedBy = "test", GrantedAt = DateTime.UtcNow });
			db.ApiTokens.Add(new ApiToken
			{
				OwnerServiceID = id, Name = "operator", TokenHash = ApiTokenFormat.Hash(token), TokenLast4 = ApiTokenFormat.Last4(token),
				Scopes = Permissions.TokensManage, CreatedBy = "test", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1)
			});
			await db.SaveChangesAsync();
			await ApiTokenCache.Reload(db);
		}

		PageResponse viaToken = await new AdminBrowser(probe).Get("/api/v1/service_accounts", Bearer(token));
		Assert.Equal(403, viaToken.Status);
		Assert.Equal(Permissions.TokensManage, Json(viaToken).GetProperty("params").GetProperty("permission").GetString());

		Assert.Equal(401, (await admin.Get("/api/v1/personal_access_tokens")).Status);
		Assert.Equal(401, (await admin.SendJson("POST", "/api/v1/personal_access_tokens", new { name = "mine", scopes = new[] { "moderation.kick" }, expires_at = InDays(30) }, csrf)).Status);
	}

	// previous behaviour: the bearer session of a registered app client manages service accounts and its own personal tokens
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAppClientSessionStillManagesTokens(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin,
			("Accounts:clients:0:client_id", "partner_portal"),
			("Accounts:clients:0:display_name", "Partner portal"),
			("Accounts:clients:0:kind", "app"),
			("Accounts:clients:0:game", "none"),
			("Accounts:clients:0:redirect_uris:0", "http://127.0.0.1/callback"));
		await admin.SeedClients();
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}

		string verifier = "a-code-verifier-long-enough-for-pkce-0123456789-abcdefghij";
		string challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		PageResponse authorized = await admin.Get($"/oauth/authorize?client_id=partner_portal&response_type=code&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcallback&scope=openid%20profile&state=s&code_challenge={challenge}&code_challenge_method=S256");
		Assert.Equal(302, authorized.Status);
		string code = System.Web.HttpUtility.ParseQueryString(new Uri(authorized.Location!).Query)["code"]!;
		PageResponse issued = await admin.Post("/oauth/token", new()
		{
			["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = "http://127.0.0.1/callback",
			["client_id"] = "partner_portal", ["code_verifier"] = verifier
		});
		Assert.Equal(200, issued.Status);
		Dictionary<string, string> bearer = Bearer(Json(issued).GetProperty("access_token").GetString()!);

		// the access token is signed with this browser's ephemeral keys, so the same pipeline reads it; any bearer value
		// keeps the bearer path even next to the sign-in cookie
		AdminBrowser api = admin;
		PageResponse created = await api.SendJson("POST", "/api/v1/service_accounts", new { name = "partner" }, bearer);
		Assert.Equal(201, created.Status);
		// the earlier fields keep their names and values; active_tokens and last_token_used_at are added
		Assert.Equal(["id", "name", "description", "created_at", "disabled_at", "permissions", "active_tokens", "last_token_used_at"], Json(created).EnumerateObject().Select(p => p.Name));
		Assert.Equal((JsonValueKind.Null, 0, JsonValueKind.Null), (Json(created).GetProperty("description").ValueKind, Json(created).GetProperty("active_tokens").GetInt32(), Json(created).GetProperty("last_token_used_at").ValueKind));
		Assert.Equal(200, (await api.Get("/api/v1/service_accounts", bearer)).Status);
		Assert.Equal(200, (await api.Get("/api/v1/personal_access_tokens", bearer)).Status);
		PageResponse mine = await api.SendJson("POST", "/api/v1/personal_access_tokens", new { name = "mine", scopes = new[] { "moderation.kick" }, expires_at = InDays(30) }, bearer);
		Assert.Equal(201, mine.Status);
		Assert.Equal(["id", "name", "scopes", "created_at", "expires_at", "last_used_at", "last_used_ip", "revoked_at", "token_last4", "allowed_ips", "token"], Json(mine).EnumerateObject().Select(p => p.Name));
		Assert.Empty(Json(mine).GetProperty("allowed_ips").EnumerateArray());
		JsonElement listed = Assert.Single(Json(await api.Get("/api/v1/personal_access_tokens", bearer)).GetProperty("personal_access_tokens").EnumerateArray());
		Assert.Equal(["id", "name", "scopes", "created_at", "expires_at", "last_used_at", "last_used_ip", "revoked_at", "token_last4", "allowed_ips"], listed.EnumerateObject().Select(p => p.Name));
		Assert.Equal(204, (await api.SendJson("DELETE", $"/api/v1/personal_access_tokens/{Json(mine).GetProperty("id").GetInt64()}", s_empty, bearer)).Status);
		Assert.Equal(AuditActorKinds.User, (await AuditOf(probe, AuditTargetKinds.ServiceAccount)).Single().ActorKind);
		Assert.Equal(chief.ID, (await AuditOf(probe, AuditTargetKinds.ServiceAccount)).Single().ActorID);
	}
}
