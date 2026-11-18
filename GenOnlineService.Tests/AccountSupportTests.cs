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

using GenOnlineService.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// Account support from the staff pages (T4): the password and two-factor resets of "GenOnlineService users ..." through
// the sign-in cookie, for accounts.support under the rank rule, audited, the new password shown in the one answer only
[Collection(AdminAreaCollection.Name)]
public class AccountSupportTests
{
	private const string OldPassword = "a long enough password";
	private const string SignedInLocation = "/oauth/authorize?client_id=x";

	private static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role));
	}

	private static async Task<User> PlayerAsync(ProbeDatabase probe, string name)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (await PasswordPagesTests.UserByEmail(probe, email))!;
	}

	// the rank rule reads the process-wide role cache
	private static async Task ReloadRoles(ProbeDatabase probe)
	{
		await using AppDbContext db = probe.Open();
		await StaffRoles.Reload(db);
	}

	private static async Task<User> Stored(ProbeDatabase probe, long id)
	{
		await using AppDbContext db = probe.Open();
		return await db.Users.AsNoTracking().SingleAsync(u => u.ID == id);
	}

	private static async Task<List<AuditLogEntry>> AuditOf(ProbeDatabase probe, string action)
	{
		await using AppDbContext db = probe.Open();
		return await db.AuditLog.AsNoTracking().Where(e => e.Action == action).OrderBy(e => e.ID).ToListAsync();
	}

	// The page's sign-in form: the location it sends the browser to after the password
	private static async Task<string?> SignInLocation(ProbeDatabase probe, string email, string password, AccountsBrowser? browser = null)
	{
		browser ??= new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		browser.SignOutLocally();
		PageResponse page = await PasswordPagesTests.SignIn(browser, email, password);
		return page.Status == 302 ? page.Location : null;
	}

	// An authenticator key protected with this browser's keys, so its sign-in pages read it
	private static async Task SetUpAuthenticator(AccountsBrowser browser, long id)
	{
		using IServiceScope scope = browser.Services.CreateScope();
		UserManager<User> users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
		User user = (await users.FindByIdAsync(id.ToString(System.Globalization.CultureInfo.InvariantCulture)))!;
		await users.ResetAuthenticatorKeyAsync(user);
		await users.SetTwoFactorEnabledAsync(user, true);
		await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
	}

	private static async Task TurnOnTwoFactor(ProbeDatabase probe, long id)
	{
		await using AppDbContext db = probe.Open();
		await db.Users.Where(u => u.ID == id).ExecuteUpdateAsync(set => set.SetProperty(u => u.TwoFactorEnabled, true).SetProperty(u => u.AuthenticatorKey, "protected-key"));
		db.UserRecoveryCodes.Add(new UserRecoveryCode { UserID = id, CodeHash = new string('a', 64), CreatedAt = DateTime.UtcNow });
		await db.SaveChangesAsync();
	}

	private static async Task<Dictionary<string, string>> ServiceToken(ProbeDatabase probe, string permission)
	{
		string token = ApiTokenFormat.Generate();
		await using AppDbContext db = probe.Open();
		var account = new ServiceAccount { Name = "support-" + Guid.NewGuid().ToString("N").Substring(0, 8), CreatedBy = "test", CreatedAt = DateTime.UtcNow };
		db.ServiceAccounts.Add(account);
		await db.SaveChangesAsync();
		db.ServiceAccountPermissions.Add(new ServiceAccountPermission { ServiceAccountID = account.ID, Permission = permission, GrantedBy = "test", GrantedAt = DateTime.UtcNow });
		db.ApiTokens.Add(new ApiToken
		{
			OwnerServiceID = account.ID,
			Name = "support",
			TokenHash = ApiTokenFormat.Hash(token),
			TokenLast4 = ApiTokenFormat.Last4(token),
			Scopes = permission,
			CreatedBy = "test",
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = DateTime.UtcNow.AddDays(1)
		});
		await db.SaveChangesAsync();
		await ApiTokenCache.Reload(db);
		return new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
	}

	// A registered game client, so a signed-in browser of the player gets a game sign-in through the OAuth endpoints
	private static readonly (string Key, string Value)[] s_gameClient =
	{
		("Accounts:clients:0:client_id", "genonline_zerohour"),
		("Accounts:clients:0:display_name", "Generals Online (Zero Hour)"),
		("Accounts:clients:0:kind", "game"),
		("Accounts:clients:0:game", "zerohour"),
		("Accounts:clients:0:redirect_uris:0", "http://127.0.0.1/callback")
	};

	private const string Verifier = "a-code-verifier-long-enough-for-pkce-0123456789-abcdefghij";

	// A player signed up through a browser that also serves the game client's OAuth endpoints
	private static async Task<(AdminBrowser Game, User User)> GamePlayerAsync(ProbeDatabase probe, string name)
	{
		var game = new AdminBrowser(probe, s_gameClient);
		await game.SeedClients();
		string email = name.ToLowerInvariant() + "@example.org";
		await game.SignUp(name, email);
		await using (AppDbContext db = probe.Open())
		{
			await ClientRegistry.Reload(db);
		}
		return (game, (await PasswordPagesTests.UserByEmail(probe, email))!);
	}

	// The game's sign-in: an authorization code with PKCE, swapped for an access and a refresh token
	private static async Task<(string Access, string Refresh)> GameSignIn(AdminBrowser game)
	{
		string challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		PageResponse authorized = await game.Get($"/oauth/authorize?client_id=genonline_zerohour&response_type=code&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcallback&scope=openid%20offline_access%20game&state=s&code_challenge={challenge}&code_challenge_method=S256");
		Assert.Equal(302, authorized.Status);
		string code = System.Web.HttpUtility.ParseQueryString(new Uri(authorized.Location!).Query)["code"]!;
		PageResponse issued = await game.Post("/oauth/token", new()
		{
			["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = "http://127.0.0.1/callback",
			["client_id"] = "genonline_zerohour", ["code_verifier"] = Verifier
		});
		Assert.Equal(200, issued.Status);
		JsonElement tokens = JsonDocument.Parse(issued.Body).RootElement;
		return (tokens.GetProperty("access_token").GetString()!, tokens.GetProperty("refresh_token").GetString()!);
	}

	private static async Task<PageResponse> Refresh(AdminBrowser game, string refresh)
		=> await game.Post("/oauth/token", new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = "genonline_zerohour" });

	// Registers the game session of an access token, as the game does after signing in; a revoked sign-in answers 401
	// before anything else is read
	private static async Task<int> RegisterSession(AdminBrowser game, string access)
		=> (await game.SendJson("POST", "/api/v1/auth/session", new { exe_crc = 1u, ini_crc = 2u, anticheat_id = 3 }, new Dictionary<string, string> { ["Authorization"] = "Bearer " + access })).Status;

	// A fresh browser signs in with the password and starts a new game sign-in
	private static async Task FreshGameSignIn(ProbeDatabase probe, string email, string password)
	{
		var game = new AdminBrowser(probe, s_gameClient);
		PageResponse page = await game.Get("/users/sign_in");
		Assert.Equal(200, page.Status);
		PageResponse signedIn = await game.Post("/users/sign_in", new() { ["__go_consent_token"] = AccountsBrowser.Token(page), ["email"] = email, ["password"] = password });
		Assert.Equal(302, signedIn.Status);
		(_, string refresh) = await GameSignIn(game);
		Assert.Equal(200, (await Refresh(game, refresh)).Status);
	}

	// After a reset the old refresh token and the old access token's game session are refused
	private static async Task AssertGameSignInEnded(AdminBrowser game, string access, string refresh)
	{
		PageResponse refused = await Refresh(game, refresh);
		Assert.Equal(400, refused.Status);
		Assert.Equal("invalid_grant", JsonDocument.Parse(refused.Body).RootElement.GetProperty("error").GetString());
		Assert.Equal(401, await RegisterSession(game, access));
	}

	// A game sign-in whose refresh and access tokens work, as the game holds them before a reset
	private static async Task<(string Access, string Refresh)> SignedInGame(AdminBrowser game)
	{
		(string access, string refresh) = await GameSignIn(game);
		PageResponse refreshed = await Refresh(game, refresh);
		Assert.Equal(200, refreshed.Status);
		refresh = JsonDocument.Parse(refreshed.Body).RootElement.GetProperty("refresh_token").GetString()!;
		// the access token authenticates here, so a later 401 at the session registration is the revoked sign-in
		Assert.Equal(200, (await game.Get("/api/v1/me", new Dictionary<string, string> { ["Authorization"] = "Bearer " + access })).Status);
		return (access, refresh);
	}

	private static int RevokedIn(AuditLogEntry entry) => JsonDocument.Parse(entry.Details!).RootElement.GetProperty("sign_ins_revoked").GetInt32();

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AStaffResetEndsEveryGameSignIn(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(AdminBrowser lostGame, User lost) = await GamePlayerAsync(probe, "Lost");
		(AdminBrowser phoneGame, User phone) = await GamePlayerAsync(probe, "Phone");
		await TurnOnTwoFactor(probe, phone.ID);
		await ReloadRoles(probe);
		(string lostAccess, string lostRefresh) = await SignedInGame(lostGame);
		(string phoneAccess, string phoneRefresh) = await SignedInGame(phoneGame);

		PageResponse reset = await admin.SendJson("POST", $"/api/v1/players/{lost.ID}/password_reset", new { reason = "lost it" }, await admin.Csrf());
		Assert.Equal(200, reset.Status);
		string password = JsonDocument.Parse(reset.Body).RootElement.GetProperty("password").GetString()!;
		await AssertGameSignInEnded(lostGame, lostAccess, lostRefresh);
		await FreshGameSignIn(probe, "lost@example.org", password);
		Assert.True(RevokedIn(Assert.Single(await AuditOf(probe, "password_reset"))) >= 1);

		Assert.Equal(204, (await admin.SendJson("POST", $"/api/v1/players/{phone.ID}/two_factor_reset", new { reason = "lost phone" }, await admin.Csrf())).Status);
		await AssertGameSignInEnded(phoneGame, phoneAccess, phoneRefresh);
		await FreshGameSignIn(probe, "phone@example.org", OldPassword);
		Assert.True(RevokedIn(Assert.Single(await AuditOf(probe, "two_factor_reset"))) >= 1);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ACliResetEndsEveryGameSignIn(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser lostGame, User lost) = await GamePlayerAsync(probe, "Lost");
		(AdminBrowser phoneGame, User phone) = await GamePlayerAsync(probe, "Phone");
		await TurnOnTwoFactor(probe, phone.ID);
		(string lostAccess, string lostRefresh) = await SignedInGame(lostGame);
		(string phoneAccess, string phoneRefresh) = await SignedInGame(phoneGame);

		string password;
		await using (AppDbContext db = probe.Open())
		{
			password = await Identity.UserResets.ResetPassword(db, lost.ID, null, 8, "cli:operator", "lost it");
			await Identity.UserResets.ResetTwoFactor(db, phone.ID, "cli:operator", "lost phone");
		}

		await AssertGameSignInEnded(lostGame, lostAccess, lostRefresh);
		await FreshGameSignIn(probe, "lost@example.org", password);
		await AssertGameSignInEnded(phoneGame, phoneAccess, phoneRefresh);
		await FreshGameSignIn(probe, "phone@example.org", OldPassword);
		Assert.True(RevokedIn(Assert.Single(await AuditOf(probe, "password_reset"))) >= 1);
		Assert.True(RevokedIn(Assert.Single(await AuditOf(probe, "two_factor_reset"))) >= 1);
	}

	// accounts.support is for staff on the staff pages: a partner service account never holds it, whoever grants it
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task APartnerIsNeverGrantedAccountSupport(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		Dictionary<string, string> csrf = await admin.Csrf();
		long id = JsonDocument.Parse((await admin.SendJson("POST", "/api/v1/service_accounts", new { name = "partner" }, csrf)).Body).RootElement.GetProperty("id").GetInt64();

		PageResponse grant = await admin.Patch($"/api/v1/service_accounts/{id}", new { grants = new { add = new[] { "accounts.support" } } }, csrf);
		Assert.Equal(403, grant.Status);
		Assert.Equal("accounts.support", JsonDocument.Parse(grant.Body).RootElement.GetProperty("params").GetProperty("permission").GetString());

		// the host's grant path refuses it too
		await using AppDbContext db = probe.Open();
		ServiceAccount account = await db.ServiceAccounts.AsTracking().SingleAsync(a => a.ID == id);
		await Assert.ThrowsAsync<Rest.ApiTokenAdminException>(() => Rest.ApiTokenAdmin.StageGrant(db, Rest.ApiTokenActor.Cli(), account, "accounts.support"));
		Assert.Equal(0, await db.ServiceAccountPermissions.CountAsync(p => p.ServiceAccountID == id));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAdminResetsAPasswordThroughTheCookieAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		User plain = await PlayerAsync(probe, "Plain");
		await ReloadRoles(probe);
		string path = $"/api/v1/players/{plain.ID}/password_reset";
		string oldHash = (await Stored(probe, plain.ID)).PasswordHash!;

		PageResponse forged = await admin.SendJson("POST", path, new { reason = "lost it" }, null);
		Assert.Equal(400, forged.Status);
		Assert.Equal("csrf", AdminArea.ErrorOf(forged));

		// the reason is required
		PageResponse noReason = await admin.SendJson("POST", path, new { }, await admin.Csrf());
		Assert.Equal(400, noReason.Status);
		Assert.Equal("invalid_argument", AdminArea.ErrorOf(noReason));
		Assert.Equal(400, (await admin.SendJson("POST", path, new { reason = "   " }, await admin.Csrf())).Status);
		Assert.Equal(oldHash, (await Stored(probe, plain.ID)).PasswordHash);
		Assert.Empty(await AuditOf(probe, "password_reset"));

		PageResponse reset = await admin.SendJson("POST", path, new { reason = "lost it" }, await admin.Csrf());
		Assert.Equal(200, reset.Status);
		string password = JsonDocument.Parse(reset.Body).RootElement.GetProperty("password").GetString()!;
		Assert.Equal(20, password.Length);

		User stored = await Stored(probe, plain.ID);
		Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<User>().VerifyHashedPassword(stored, stored.PasswordHash!, password));
		Assert.NotEqual(plain.SecurityStamp, stored.SecurityStamp);
		Assert.Null(await SignInLocation(probe, "plain@example.org", OldPassword));
		Assert.Equal(SignedInLocation, await SignInLocation(probe, "plain@example.org", password));

		AuditLogEntry entry = Assert.Single(await AuditOf(probe, "password_reset"));
		Assert.Equal((AuditActorKinds.User, (long?)chief.ID, $"user:{chief.ID}", (long?)plain.ID, "lost it", "api"), (entry.ActorKind, entry.ActorID, entry.Actor, entry.TargetID, entry.Reason, entry.Source));

		// the password is in that one answer only
		await using AppDbContext db = probe.Open();
		List<AuditLogEntry> all = await db.AuditLog.AsNoTracking().ToListAsync();
		Assert.DoesNotContain(all, e => (e.Details ?? "").Contains(password) || (e.Reason ?? "").Contains(password) || e.Actor.Contains(password));
		Assert.DoesNotContain(password, (await admin.Get($"/api/v1/players/{plain.ID}/staff_view")).Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAdminResetsTwoFactorThroughTheCookieAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		User guarded = await PlayerAsync(probe, "Guarded");
		var pages = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		await SetUpAuthenticator(pages, guarded.ID);
		guarded = await Stored(probe, guarded.ID);
		await ReloadRoles(probe);
		string path = $"/api/v1/players/{guarded.ID}/two_factor_reset";
		Assert.StartsWith("/users/two_factor", await SignInLocation(probe, "guarded@example.org", OldPassword, pages));

		PageResponse forged = await admin.SendJson("POST", path, new { reason = "lost phone" }, null);
		Assert.Equal(400, forged.Status);
		Assert.Equal("csrf", AdminArea.ErrorOf(forged));
		Assert.Equal(400, (await admin.SendJson("POST", path, new { }, await admin.Csrf())).Status);
		Assert.True((await Stored(probe, guarded.ID)).TwoFactorEnabled);

		Assert.Equal(204, (await admin.SendJson("POST", path, new { reason = "lost phone" }, await admin.Csrf())).Status);
		User stored = await Stored(probe, guarded.ID);
		Assert.False(stored.TwoFactorEnabled);
		Assert.Null(stored.AuthenticatorKey);
		Assert.NotEqual(guarded.SecurityStamp, stored.SecurityStamp);
		Assert.Equal(guarded.PasswordHash, stored.PasswordHash);
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(0, await db.UserRecoveryCodes.CountAsync(c => c.UserID == guarded.ID));
		}
		Assert.Equal(SignedInLocation, await SignInLocation(probe, "guarded@example.org", OldPassword, pages));

		AuditLogEntry entry = Assert.Single(await AuditOf(probe, "two_factor_reset"));
		Assert.Equal((AuditActorKinds.User, (long?)chief.ID, (long?)guarded.ID, "lost phone", "api"), (entry.ActorKind, entry.ActorID, entry.TargetID, entry.Reason, entry.Source));

		// now without two-factor sign-in, a second reset has nothing to remove
		PageResponse again = await admin.SendJson("POST", path, new { reason = "lost phone" }, await admin.Csrf());
		Assert.Equal(422, again.Status);
		Assert.Equal("account.no_two_factor", AdminArea.ErrorOf(again));
		Assert.Single(await AuditOf(probe, "two_factor_reset"));
	}

	// a provider-only account has no password to reset; an unknown id is not found
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnAccountWithoutAPasswordAnswersItsCode(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, _) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		User providerOnly = await PlayerAsync(probe, "Linked");
		await using (AppDbContext db = probe.Open())
		{
			await db.Users.Where(u => u.ID == providerOnly.ID).ExecuteUpdateAsync(set => set.SetProperty(u => u.Email, (string?)null).SetProperty(u => u.EmailNormalized, (string?)null));
		}
		await ReloadRoles(probe);

		PageResponse refused = await admin.SendJson("POST", $"/api/v1/players/{providerOnly.ID}/password_reset", new { reason = "lost it" }, await admin.Csrf());
		Assert.Equal(422, refused.Status);
		Assert.Equal("account.no_password", AdminArea.ErrorOf(refused));
		Assert.Equal(404, (await admin.SendJson("POST", "/api/v1/players/999999/password_reset", new { reason = "lost it" }, await admin.Csrf())).Status);
		Assert.Equal(404, (await admin.SendJson("POST", "/api/v1/players/999999/two_factor_reset", new { reason = "lost it" }, await admin.Csrf())).Status);
		Assert.Empty(await AuditOf(probe, "password_reset"));
	}

	// accounts.support is the admin's; the rank rule keeps staff off themselves, their peers and their seniors; a token
	// holding the permission still never reaches these routes
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task OnlyAnAdminActsOnALowerRank(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		(AdminBrowser moderator, User warden) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		(_, User peer) = await StaffAsync(probe, "Peer", StaffRoles.Admin);
		(_, User boss) = await StaffAsync(probe, "Boss", StaffRoles.Owner);
		User plain = await PlayerAsync(probe, "Plain");
		await TurnOnTwoFactor(probe, plain.ID);
		await ReloadRoles(probe);

		foreach (string action in new[] { "password_reset", "two_factor_reset" })
		{
			PageResponse notPermitted = await moderator.SendJson("POST", $"/api/v1/players/{plain.ID}/{action}", new { reason = "x" }, await moderator.Csrf());
			Assert.Equal(403, notPermitted.Status);
			Assert.Equal("forbidden", AdminArea.ErrorOf(notPermitted));

			PageResponse self = await admin.SendJson("POST", $"/api/v1/players/{chief.ID}/{action}", new { reason = "x" }, await admin.Csrf());
			Assert.Equal(400, self.Status);
			Assert.Equal("invalid_argument", AdminArea.ErrorOf(self));

			foreach (long senior in new[] { peer.ID, boss.ID })
			{
				PageResponse outranked = await admin.SendJson("POST", $"/api/v1/players/{senior}/{action}", new { reason = "x" }, await admin.Csrf());
				Assert.Equal(403, outranked.Status);
				Assert.Equal("moderation.target_outranks", AdminArea.ErrorOf(outranked));
			}

			PageResponse token = await new AdminBrowser(probe).SendJson("POST", $"/api/v1/players/{plain.ID}/{action}", new { reason = "x" }, await ServiceToken(probe, "accounts.support"));
			Assert.Equal(403, token.Status);
		}

		// the moderator is below the admin, so an admin helps a moderator too
		Assert.Equal(200, (await admin.SendJson("POST", $"/api/v1/players/{warden.ID}/password_reset", new { reason = "locked out" }, await admin.Csrf())).Status);

		User stored = await Stored(probe, plain.ID);
		Assert.True(stored.TwoFactorEnabled);
		Assert.Equal(plain.PasswordHash, stored.PasswordHash);
		Assert.Single(await AuditOf(probe, "password_reset"));
		Assert.Empty(await AuditOf(probe, "two_factor_reset"));
	}
}
