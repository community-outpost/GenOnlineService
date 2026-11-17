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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// The email + password pages (identity hub T3b) through the real MVC pipeline against a real database: sign-up, sign-in,
// lockout, refusals and the antiforgery token every form needs
public class PasswordPagesTests
{
	internal static Dictionary<string, string?> Enabled(params (string Key, string Value)[] extra)
	{
		Dictionary<string, string?> values = new() { ["Accounts:password:enabled"] = "true" };
		foreach ((string key, string value) in extra)
		{
			values[key] = value;
		}
		return values;
	}

	internal static async Task<PageResponse> SignUp(AccountsBrowser browser, string name, string email, string password, string? honeypot = null)
	{
		PageResponse page = await browser.Get("/users/sign_up?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dx");
		Assert.Equal(200, page.Status);
		Dictionary<string, string> form = new()
		{
			["__go_consent_token"] = AccountsBrowser.Token(page),
			["display_name"] = name,
			["email"] = email,
			["password"] = password,
			["ReturnUrl"] = "/oauth/authorize?client_id=x"
		};
		if (honeypot != null)
		{
			form["website"] = honeypot;
		}
		return await browser.Post("/users/sign_up", form);
	}

	internal static async Task<PageResponse> SignIn(AccountsBrowser browser, string email, string password)
	{
		PageResponse page = await browser.Get("/users/sign_in?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dx");
		Assert.Equal(200, page.Status);
		return await browser.Post("/users/sign_in", new()
		{
			["__go_consent_token"] = AccountsBrowser.Token(page),
			["email"] = email,
			["password"] = password,
			["ReturnUrl"] = "/oauth/authorize?client_id=x"
		});
	}

	internal static async Task<User?> UserByEmail(ProbeDatabase probe, string email)
	{
		await using AppDbContext db = probe.Open();
		string normalized = NameNormalizer.Normalize(email);
		return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.EmailNormalized == normalized);
	}

	// sign-up creates the account (email unverified without mail), signs in and goes back where the game was
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignUpCreatesTheAccountAndSignsIn(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, Enabled());

		PageResponse done = await SignUp(browser, "Newcomer", "new@example.org", "a long enough password");

		Assert.Equal(302, done.Status);
		Assert.Equal("/oauth/authorize?client_id=x", done.Location);
		Assert.True(browser.SignedIn);
		User? user = await UserByEmail(probe, "NEW@example.org");
		Assert.NotNull(user);
		Assert.Equal("Newcomer", user!.Username);
		Assert.InRange(user.NameTag, 1, 9999);
		Assert.Null(user.EmailConfirmedAt);
		Assert.NotNull(user.PasswordHash);
		Assert.Equal(UserStates.Active, user.State);
	}

	// display names are not unique: two people may both be "Player", and each can still be updated (lockout counts)
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TwoAccountsMayShareADisplayName(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		Assert.Equal(302, (await SignUp(new AccountsBrowser(probe, Enabled()), "Player", "one@example.org", "first long password")).Status);
		Assert.Equal(302, (await SignUp(new AccountsBrowser(probe, Enabled()), "Player", "two@example.org", "second long password")).Status);

		Assert.Equal(401, (await SignIn(new AccountsBrowser(probe, Enabled()), "two@example.org", "wrong password here")).Status);
		Assert.Equal(1, (await UserByEmail(probe, "two@example.org"))!.FailedSignInCount);
		User one = (await UserByEmail(probe, "one@example.org"))!;
		User two = (await UserByEmail(probe, "two@example.org"))!;
		Assert.NotEqual(one.NameTag, two.NameTag);
	}

	public static IEnumerable<object[]> Refusals()
	{
		foreach (object[] provider in ProbeDatabase.Providers())
		{
			// name, email, password, honeypot, expected message
			yield return new object[] { provider[0], "Bot", "bot@example.org", "a long enough password", "http://spam.test", "Sign-up failed." };
			yield return new object[] { provider[0], "Denied", "someone@Mailinator.com", "a long enough password", "", "This email address cannot be used." };
			yield return new object[] { provider[0], "Denied", "someone@sub.mailinator.com", "a long enough password", "", "This email address cannot be used." };
			yield return new object[] { provider[0], "NoMail", "not an email", "a long enough password", "", "Enter a valid email address." };
			yield return new object[] { provider[0], "Short", "short@example.org", "short", "", "Passwords must be at least 8 characters." };
			yield return new object[] { provider[0], "Long", "long@example.org", new string('p', 129), "", "Passwords must be at most 128 characters." };
			yield return new object[] { provider[0], "TheAdmin", "admin@example.org", "a long enough password", "", "This name contains a reserved word." };
			yield return new object[] { provider[0], "Bad#Name", "hash@example.org", "a long enough password", "", "This name has characters that are not allowed." };
		}
	}

	[ProbeTheory]
	[MemberData(nameof(Refusals))]
	public async Task SignUpRefusesBadInput(string provider, string name, string email, string password, string honeypot, string message)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, Enabled(("Accounts:password:email_domain_deny_list:0", "mailinator.com")));

		PageResponse refused = await SignUp(browser, name, email, password, honeypot);

		Assert.Equal(400, refused.Status);
		Assert.Contains(WebUtility.HtmlEncode(message), refused.Body);
		Assert.False(browser.SignedIn);
		await using AppDbContext db = probe.Open();
		Assert.Equal(0, await db.Users.CountAsync());
	}

	// a second account with the same email address is refused
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnEmailAddressHoldsOneAccount(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Assert.Equal(302, (await SignUp(new AccountsBrowser(probe, Enabled()), "First", "same@example.org", "a long enough password")).Status);

		PageResponse second = await SignUp(new AccountsBrowser(probe, Enabled()), "Second", "Same@Example.org", "another long password");

		Assert.Equal(400, second.Status);
		Assert.Contains("An account with this email address already exists.", second.Body);
		await using AppDbContext db = probe.Open();
		Assert.Equal(1, await db.Users.CountAsync());
	}

	// a form posted without its antiforgery token changes nothing
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task FormsNeedTheirAntiforgeryToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, Enabled());

		PageResponse signUp = await browser.Post("/users/sign_up", new() { ["display_name"] = "Forged", ["email"] = "forged@example.org", ["password"] = "a long enough password" });
		PageResponse signIn = await browser.Post("/users/sign_in", new() { ["email"] = "forged@example.org", ["password"] = "a long enough password" });

		Assert.Equal(400, signUp.Status);
		Assert.Equal(400, signIn.Status);
		await using AppDbContext db = probe.Open();
		Assert.Equal(0, await db.Users.CountAsync());
	}

	// the right password signs in; a wrong one, an unknown email and a locked account all get the same answer
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignInWithPasswordAndLockout(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Assert.Equal(302, (await SignUp(new AccountsBrowser(probe, Enabled()), "Signer", "signer@example.org", "the right password")).Status);

		var browser = new AccountsBrowser(probe, Enabled());
		PageResponse right = await SignIn(browser, "SIGNER@example.org", "the right password");
		Assert.Equal(302, right.Status);
		Assert.Equal("/oauth/authorize?client_id=x", right.Location);
		Assert.True(browser.SignedIn);

		PageResponse unknown = await SignIn(new AccountsBrowser(probe, Enabled()), "nobody@example.org", "the right password");
		Assert.Equal(401, unknown.Status);
		Assert.Contains("Wrong email or password.", unknown.Body);

		for (int attempt = 1; attempt <= 5; attempt++)
		{
			PageResponse wrong = await SignIn(new AccountsBrowser(probe, Enabled()), "signer@example.org", "a wrong password");
			Assert.Equal(401, wrong.Status);
			Assert.Contains("Wrong email or password.", wrong.Body);
		}

		var locked = new AccountsBrowser(probe, Enabled());
		PageResponse lockedOut = await SignIn(locked, "signer@example.org", "the right password");
		Assert.Equal(401, lockedOut.Status);
		Assert.Contains("Wrong email or password.", lockedOut.Body);
		Assert.False(locked.SignedIn);
		Assert.NotNull((await UserByEmail(probe, "signer@example.org"))!.LockoutEndAt);
	}

	// a blocked or deactivated account is refused once the password is right
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task BlockedAccountsAreRefused(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Assert.Equal(302, (await SignUp(new AccountsBrowser(probe, Enabled()), "Blocked", "blocked@example.org", "the right password")).Status);
		await using (AppDbContext db = probe.Open())
		{
			await db.Users.Where(u => u.Email == "blocked@example.org").ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UserStates.Blocked));
		}

		var browser = new AccountsBrowser(probe, Enabled());
		PageResponse refused = await SignIn(browser, "blocked@example.org", "the right password");

		Assert.Equal(403, refused.Status);
		Assert.Contains("This account is blocked.", refused.Body);
		Assert.False(browser.SignedIn);
	}

	// until two-factor sign-in exists, an account with it on is never signed in by its password alone
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task APasswordAloneNeverPassesTwoFactor(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Assert.Equal(302, (await SignUp(new AccountsBrowser(probe, Enabled()), "Guarded", "guarded@example.org", "the right password")).Status);
		await using (AppDbContext db = probe.Open())
		{
			await db.Users.Where(u => u.Email == "guarded@example.org").ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorEnabled, true));
		}

		var browser = new AccountsBrowser(probe, Enabled());
		PageResponse response = await SignIn(browser, "guarded@example.org", "the right password");

		Assert.False(browser.SignedIn);
		Assert.NotEqual("/oauth/authorize?client_id=x", response.Location);
	}

	// the sign-in page offers the password form next to the providers; sign-up only where it is open
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task PagesFollowTheSettings(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		PageResponse withPassword = await new AccountsBrowser(probe, Enabled(("Accounts:providers:steam:enabled", "true"), ("Accounts:providers:steam:only_provider", "true"))).Get("/users/sign_in");
		Assert.Equal(200, withPassword.Status);
		Assert.Contains("action=\"/users/sign_in\"", withPassword.Body);
		Assert.Contains("href=\"/users/sign_up", withPassword.Body);
		Assert.Contains("Sign in with Steam</a>", withPassword.Body);
		Assert.Equal("DENY", withPassword.FrameOptions);

		var closed = new AccountsBrowser(probe, Enabled(("Accounts:password:allow_sign_up", "false")));
		PageResponse closedSignIn = await closed.Get("/users/sign_in");
		Assert.DoesNotContain("href=\"/users/sign_up", closedSignIn.Body);
		Assert.Equal(404, (await closed.Get("/users/sign_up")).Status);
		Assert.Equal(404, (await closed.Post("/users/sign_up", new())).Status);

		var off = new AccountsBrowser(probe, new());
		Assert.Equal(503, (await off.Get("/users/sign_in")).Status);
		Assert.Equal(404, (await off.Get("/users/sign_up")).Status);
		Assert.Equal(404, (await off.Post("/users/sign_in", new())).Status);
	}
}
