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
