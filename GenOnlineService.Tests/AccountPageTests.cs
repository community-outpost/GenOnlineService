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
using Xunit;

namespace GenOnlineService.Tests;

// /profile, each environment's own player page (S12): who you are, a rename with the rules of PATCH /api/v1/me, how you
// sign in, and sign out
public class AccountPageTests
{
	private static async Task<(AccountsBrowser Browser, User User)> SignedUp(ProbeDatabase probe, string name, string email)
	{
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled(("Accounts:providers:discord:enabled", "true"),
			("Accounts:providers:discord:client_id", "d-id"), ("Accounts:providers:discord:client_secret", "d-secret")));
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, name, email, "a long enough password")).Status);
		return (browser, (await PasswordPagesTests.UserByEmail(probe, email))!);
	}

	private static async Task<PageResponse> Rename(AccountsBrowser browser, string name)
	{
		PageResponse page = await browser.Get("/profile");
		Assert.Equal(200, page.Status);
		return await browser.Post("/profile", new() { ["__go_consent_token"] = AccountsBrowser.Token(page), ["display_name"] = name });
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignedOutGoesToSignInFirst(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());

		PageResponse page = await browser.Get("/profile");
		Assert.Equal(302, page.Status);
		// the cookie handler's login path, absolute, coming back here afterwards
		Assert.Equal("http://accounts.test/users/sign_in?ReturnUrl=%2Fprofile", page.Location);
	}

	// a sign-up or sign-in that came from nowhere (no ReturnUrl) lands on the account page, not on an empty /
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ADirectSignUpLandsOnTheAccount(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());

		PageResponse page = await browser.Get("/users/sign_up");
		PageResponse created = await browser.Post("/users/sign_up", new()
		{
			["__go_consent_token"] = AccountsBrowser.Token(page),
			["display_name"] = "Direct",
			["email"] = "direct@example.org",
			["password"] = "a long enough password"
		});
		Assert.Equal(302, created.Status);
		Assert.Equal("/profile", created.Location);
		Assert.Contains("href=\"/profile\" class=\"go-logo\"", page.Body);
	}

	// the name with its tag, the player id, the address and whether two-factor sign-in is on, a link to set it up, sign out
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ThePageShowsTheAccount(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await SignedUp(probe, "Viewer", "viewer@example.org");

		PageResponse page = await browser.Get("/profile");
		Assert.Equal(200, page.Status);
		Assert.Contains($"Viewer<span class=\"go-tag\">#{Database.DisplayNames.FormatTag(user.NameTag)}</span>", page.Body);
		Assert.Contains($"Player id {user.ID}", page.Body);
		Assert.Contains("viewer@example.org", page.Body);
		Assert.Contains("Two-factor sign-in is off.", page.Body);
		Assert.Contains("href=\"/profile/two_factor_auth?ReturnUrl=%2Fprofile\"", page.Body);
		Assert.Contains("action=\"/users/sign_out\"", page.Body);
		Assert.Contains("No other sign-in is linked.", page.Body);
	}

	// a rename gives the player's tag for the new name, answers with the new name and reissues the cookie; the page says so once
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ARenameTakesThePlayersTagForTheName(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await SignedUp(probe, "Before", "renamer@example.org");

		PageResponse renamed = await Rename(browser, "After");
		Assert.Equal(302, renamed.Status);
		Assert.Equal("/profile?renamed=1", renamed.Location);

		User after = (await PasswordPagesTests.UserByEmail(probe, "renamer@example.org"))!;
		Assert.Equal("After", after.Username);
		Assert.Equal(GenOnlineService.Names.NameTags.Candidates(user.ID, GenOnlineService.Names.NameKey.Of("After")).First(), after.NameTag);
		Assert.Contains($"Your name is now After#{Database.DisplayNames.FormatTag(after.NameTag)}", (await browser.Get("/profile?renamed=1")).Body);
	}

	// the rules of a rename refuse with the message under the field and change nothing
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ARefusedRenameSaysWhy(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await SignedUp(probe, "Keeper", "keeper@example.org");

		PageResponse tooShort = await Rename(browser, "ab");
		Assert.Equal(400, tooShort.Status);
		Assert.Contains("Names must be 3 to 16 characters.", tooShort.Body);
		Assert.Contains("value=\"ab\"", tooShort.Body);
		Assert.Equal("Keeper", (await PasswordPagesTests.UserByEmail(probe, "keeper@example.org"))!.Username);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ARenameWithoutTheFormTokenIsRefused(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await SignedUp(probe, "Guarded", "guarded@example.org");

		Assert.Equal(400, (await browser.Post("/profile", new() { ["display_name"] = "Forged" })).Status);
		Assert.Equal("Guarded", (await PasswordPagesTests.UserByEmail(probe, "guarded@example.org"))!.Username);
	}

	// each linked provider by its name, with when it was linked and last used
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task LinkedSignInsAreListed(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await SignedUp(probe, "Linked", "linked@example.org");
		await using (AppDbContext db = probe.Open())
		{
			db.Identities.Add(new UserIdentity { UserID = user.ID, Provider = "discord", ExternUid = "80351110224678913", CreatedAt = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), LastUsedAt = new DateTime(2026, 11, 16, 8, 30, 0, DateTimeKind.Utc) });
			await db.SaveChangesAsync();
		}

		PageResponse page = await browser.Get("/profile");
		Assert.Contains(">Discord<", page.Body);
		Assert.Contains("linked 2026-11-01, last used 2026-11-16", page.Body);
		Assert.DoesNotContain("80351110224678913", page.Body);
	}

	// the top bar links to the staff pages for a holder of a staff role, and only for one (S12, T14b)
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task OnlyStaffSeeTheAdminLink(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser player, _) = await SignedUp(probe, "Plain", "plain@example.org");
		(AccountsBrowser staff, User moderator) = await SignedUp(probe, "Warden", "warden@example.org");
		await using (AppDbContext db = probe.Open())
		{
			db.UserRoles.Add(new UserRole { UserID = moderator.ID, Role = StaffRoles.Moderator, Source = AuthoritySources.Local, GrantedAt = DateTime.UtcNow, GrantedBy = "test" });
			await db.SaveChangesAsync();
		}

		PageResponse playerPage = await player.Get("/profile");
		Assert.Equal(200, playerPage.Status);
		Assert.DoesNotContain("/admin", playerPage.Body);

		PageResponse staffPage = await staff.Get("/profile");
		Assert.Equal(200, staffPage.Status);
		Assert.Contains("href=\"/admin/reports\"", staffPage.Body);
		Assert.Contains(">Admin</a>", staffPage.Body);
	}
}
