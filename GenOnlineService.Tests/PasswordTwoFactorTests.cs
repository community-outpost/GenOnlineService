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
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// Two-factor sign-in for email + password accounts (identity hub T3c): setting up an authenticator app, signing in with
// a code or a recovery code, and staff who sign in with a password having to set it up before any token is issued
public class PasswordTwoFactorTests
{
	// RFC 6238 with the authenticator defaults: SHA-1, 30-second steps, 6 digits
	private static string Totp(string base32Key, long? step = null)
	{
		string clean = base32Key.Replace(" ", String.Empty).TrimEnd('=').ToUpperInvariant();
		var bytes = new List<byte>();
		int buffer = 0, bits = 0;
		foreach (char c in clean)
		{
			buffer = (buffer << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c);
			bits += 5;
			if (bits >= 8)
			{
				bytes.Add((byte)(buffer >> (bits - 8)));
				bits -= 8;
			}
		}

		long counter = step ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
		byte[] message = BitConverter.GetBytes(counter);
		if (BitConverter.IsLittleEndian)
		{
			Array.Reverse(message);
		}
		byte[] hash = HMACSHA1.HashData(bytes.ToArray(), message);
		int offset = hash[^1] & 0x0F;
		int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
		return (binary % 1_000_000).ToString("D6");
	}

	private static string KeyOf(PageResponse setup)
	{
		System.Text.RegularExpressions.Match match = Regex.Match(setup.Body, "secret=([A-Z2-7]+)");
		Assert.True(match.Success, "the setup page shows no otpauth secret: " + setup.Body);
		return match.Groups[1].Value;
	}

	private static List<string> RecoveryCodesOf(PageResponse page) =>
		Regex.Matches(page.Body, "<li><code>([^<]+)</code></li>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();

	// signs up a password account and turns two-factor sign-in on; returns the key and the recovery codes shown
	private static async Task<(string Key, List<string> Codes)> SetUp(ProbeDatabase probe, string email)
	{
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Guarded", email, "the right password")).Status);

		PageResponse setup = await browser.Get("/profile/two_factor_auth?ReturnUrl=%2Fdone");
		Assert.Equal(200, setup.Status);
		Assert.Contains("otpauth://totp/", setup.Body);
		string key = KeyOf(setup);

		PageResponse enabled = await browser.Post("/profile/two_factor_auth", new()
		{
			["__go_consent_token"] = AccountsBrowser.Token(setup),
			["code"] = Totp(key),
			["ReturnUrl"] = "/done"
		});
		Assert.Equal(200, enabled.Status);
		List<string> codes = RecoveryCodesOf(enabled);
		Assert.Equal(10, codes.Count);
		Assert.Contains("href=\"/done\"", enabled.Body);
		return (key, codes);
	}

	private static async Task<PageResponse> SecondFactor(AccountsBrowser browser, string field, string value)
	{
		PageResponse page = await browser.Get("/users/two_factor?ReturnUrl=%2Fdone");
		Assert.Equal(200, page.Status);
		return await browser.Post("/users/two_factor", new()
		{
			["__go_consent_token"] = AccountsBrowser.Token(page),
			[field] = value,
			["ReturnUrl"] = "/done"
		});
	}

	// set-up checks a first code, then stores the flag and 10 hashed recovery codes
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SetUpTurnsTwoFactorOn(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		await SetUp(probe, "setup@example.org");

		User user = (await PasswordPagesTests.UserByEmail(probe, "setup@example.org"))!;
		Assert.True(user.TwoFactorEnabled);
		Assert.NotNull(user.AuthenticatorKey);
		await using AppDbContext db = probe.Open();
		Assert.Equal(10, await db.UserRecoveryCodes.CountAsync(c => c.UserID == user.ID && c.UsedAt == null));
	}

	// a wrong first code leaves two-factor sign-in off
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AWrongFirstCodeChangesNothing(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Typo", "typo@example.org", "the right password")).Status);
		PageResponse setup = await browser.Get("/profile/two_factor_auth");
		string wrong = Totp(KeyOf(setup)) == "000000" ? "111111" : "000000";

		PageResponse refused = await browser.Post("/profile/two_factor_auth", new() { ["__go_consent_token"] = AccountsBrowser.Token(setup), ["code"] = wrong });

		Assert.Equal(400, refused.Status);
		Assert.Contains("Wrong code.", refused.Body);
		Assert.False((await PasswordPagesTests.UserByEmail(probe, "typo@example.org"))!.TwoFactorEnabled);
	}

	// setting up needs a signed-in password account
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SetUpNeedsASignedInUser(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		PageResponse anonymous = await new AccountsBrowser(probe, PasswordPagesTests.Enabled()).Get("/profile/two_factor_auth");

		Assert.Equal(302, anonymous.Status);
		Assert.StartsWith("http://accounts.test/users/sign_in", anonymous.Location);

		// the setup page lives under the profile, as GitLab's does; the old paths are gone
		var old = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		Assert.Equal(404, (await old.Get("/users/two_factor/setup")).Status);
		Assert.Equal(404, (await old.Post("/users/two_factor/setup", new())).Status);
		Assert.Equal(404, (await old.Post("/users/two_factor/disable", new())).Status);
	}

	// the password leads to the code page; the right code signs in, a wrong one does not and counts toward the lockout
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignInAsksForTheCode(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(string key, _) = await SetUp(probe, "code@example.org");

		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		PageResponse password = await PasswordPagesTests.SignIn(browser, "code@example.org", "the right password");
		Assert.Equal(302, password.Status);
		Assert.Equal("/users/two_factor?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dx", password.Location);
		Assert.False(browser.SignedIn);

		string wrong = Totp(key) == "000000" ? "111111" : "000000";
		PageResponse refused = await SecondFactor(browser, "code", wrong);
		Assert.Equal(401, refused.Status);
		Assert.Contains("Wrong code.", refused.Body);
		Assert.False(browser.SignedIn);
		Assert.Equal(1, (await PasswordPagesTests.UserByEmail(probe, "code@example.org"))!.FailedSignInCount);

		PageResponse right = await SecondFactor(browser, "code", Totp(key));
		Assert.Equal(302, right.Status);
		Assert.Equal("/done", right.Location);
		Assert.True(browser.SignedIn);
	}

	// the code page is only for someone who just gave the right password
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheCodePageNeedsThePasswordFirst(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(string key, _) = await SetUp(probe, "first@example.org");
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());

		PageResponse page = await browser.Get("/users/two_factor");

		Assert.Equal(302, page.Status);
		Assert.StartsWith("/users/sign_in", page.Location);
		Assert.False(browser.SignedIn);
	}

	// a recovery code signs in once
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ARecoveryCodeSignsInOnce(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(_, List<string> codes) = await SetUp(probe, "recover@example.org");

		var first = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		Assert.Equal(302, (await PasswordPagesTests.SignIn(first, "recover@example.org", "the right password")).Status);
		Assert.Equal(302, (await SecondFactor(first, "recovery_code", codes[0])).Status);
		Assert.True(first.SignedIn);

		var again = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		Assert.Equal(302, (await PasswordPagesTests.SignIn(again, "recover@example.org", "the right password")).Status);
		Assert.Equal(401, (await SecondFactor(again, "recovery_code", codes[0])).Status);
		Assert.False(again.SignedIn);
	}

	// turning it off needs a current code
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TurningItOffNeedsACode(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(string key, _) = await SetUp(probe, "off@example.org");
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		Assert.Equal(302, (await PasswordPagesTests.SignIn(browser, "off@example.org", "the right password")).Status);
		Assert.Equal(302, (await SecondFactor(browser, "code", Totp(key))).Status);

		PageResponse manage = await browser.Get("/profile/two_factor_auth");
		Assert.Equal(200, manage.Status);
		string wrong = Totp(key) == "000000" ? "111111" : "000000";
		Assert.Equal(400, (await browser.Post("/profile/two_factor_auth/disable", new() { ["__go_consent_token"] = AccountsBrowser.Token(manage), ["code"] = wrong })).Status);
		Assert.True((await PasswordPagesTests.UserByEmail(probe, "off@example.org"))!.TwoFactorEnabled);

		PageResponse off = await browser.Post("/profile/two_factor_auth/disable", new() { ["__go_consent_token"] = AccountsBrowser.Token(manage), ["code"] = Totp(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1) });
		Assert.Equal(200, off.Status);
		User user = (await PasswordPagesTests.UserByEmail(probe, "off@example.org"))!;
		Assert.False(user.TwoFactorEnabled);
		await using AppDbContext db = probe.Open();
		Assert.Equal(0, await db.UserRecoveryCodes.CountAsync(c => c.UserID == user.ID && c.UsedAt == null));
	}

	private const string AuthorizeQuery = "?client_id=genonline_zerohour&response_type=code&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcallback&scope=openid%20game&state=s&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";

	private static Dictionary<string, string?> WithGameClient() => PasswordPagesTests.Enabled(
		("Accounts:clients:0:client_id", "genonline_zerohour"),
		("Accounts:clients:0:display_name", "Generals Online (Zero Hour)"),
		("Accounts:clients:0:kind", "game"),
		("Accounts:clients:0:game", "zerohour"),
		("Accounts:clients:0:redirect_uris:0", "http://127.0.0.1/callback"));

	private static async Task MakeStaff(ProbeDatabase probe, string email, string role)
	{
		User user = (await PasswordPagesTests.UserByEmail(probe, email))!;
		await using AppDbContext db = probe.Open();
		db.UserRoles.Add(new UserRole { UserID = user.ID, Role = role, Source = AuthoritySources.Local, GrantedAt = DateTime.UtcNow, GrantedBy = "test" });
		await db.SaveChangesAsync();
	}

	// staff who signed in with only a password are sent to set up two-factor sign-in before any token is issued;
	// a role without permissions is not staff
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffMustSetItUpBeforeTokens(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var signUp = new AccountsBrowser(probe, WithGameClient());
		await AccountsSetup.SeedClients(signUp.Services, signUp.Settings);
		Assert.Equal(302, (await PasswordPagesTests.SignUp(signUp, "Mod", "mod@example.org", "the right password")).Status);
		Assert.Equal(302, (await PasswordPagesTests.SignUp(new AccountsBrowser(probe, WithGameClient()), "Streamer", "streamer@example.org", "the right password")).Status);
		await MakeStaff(probe, "mod@example.org", "moderator");
		await MakeStaff(probe, "streamer@example.org", "streamer");

		var moderator = new AccountsBrowser(probe, WithGameClient());
		Assert.Equal(302, (await PasswordPagesTests.SignIn(moderator, "mod@example.org", "the right password")).Status);
		PageResponse gated = await moderator.Get("/oauth/authorize" + AuthorizeQuery);
		Assert.Equal(302, gated.Status);
		Assert.StartsWith("/profile/two_factor_auth?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dgenonline_zerohour", gated.Location);

		var streamer = new AccountsBrowser(probe, WithGameClient());
		Assert.Equal(302, (await PasswordPagesTests.SignIn(streamer, "streamer@example.org", "the right password")).Status);
		await using (AppDbContext db = probe.Open())
		{
			// the process-wide client registry the service reloads every few seconds
			await ClientRegistry.Reload(db);
		}
		PageResponse passed = await streamer.Get("/oauth/authorize" + AuthorizeQuery);
		Assert.Equal(302, passed.Status);
		Assert.StartsWith("http://127.0.0.1/callback?code=", passed.Location);
	}

	// staff cannot turn it off
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffKeepTwoFactor(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(string key, _) = await SetUp(probe, "admin-off@example.org");
		await MakeStaff(probe, "admin-off@example.org", "admin");
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		Assert.Equal(302, (await PasswordPagesTests.SignIn(browser, "admin-off@example.org", "the right password")).Status);
		Assert.Equal(302, (await SecondFactor(browser, "code", Totp(key))).Status);

		PageResponse manage = await browser.Get("/profile/two_factor_auth");
		PageResponse refused = await browser.Post("/profile/two_factor_auth/disable", new() { ["__go_consent_token"] = AccountsBrowser.Token(manage), ["code"] = Totp(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + 1) });

		Assert.Equal(403, refused.Status);
		Assert.Contains("Staff accounts keep two-factor sign-in.", refused.Body);
		Assert.True((await PasswordPagesTests.UserByEmail(probe, "admin-off@example.org"))!.TwoFactorEnabled);
	}

	// the gate does not depend on how the cookie says it signed in: re-validating the cookie (every minute in service,
	// at once here) rebuilds it, and a password-only moderator still gets no code
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheStaffGateSurvivesCookieRevalidation(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		static void RevalidateAlways(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
			=> Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.PostConfigure<Microsoft.AspNetCore.Identity.SecurityStampValidatorOptions>(services, o => o.ValidationInterval = TimeSpan.Zero);
		var signUp = new AccountsBrowser(probe, WithGameClient(), RevalidateAlways);
		await AccountsSetup.SeedClients(signUp.Services, signUp.Settings);
		Assert.Equal(302, (await PasswordPagesTests.SignUp(signUp, "Mod", "revalidated@example.org", "the right password")).Status);
		await MakeStaff(probe, "revalidated@example.org", "moderator");

		var moderator = new AccountsBrowser(probe, WithGameClient(), RevalidateAlways);
		Assert.Equal(302, (await PasswordPagesTests.SignIn(moderator, "revalidated@example.org", "the right password")).Status);
		await using (AppDbContext db = probe.Open())
		{
			await ClientRegistry.Reload(db);
		}
		for (int request = 0; request < 2; request++)
		{
			PageResponse gated = await moderator.Get("/oauth/authorize" + AuthorizeQuery);
			Assert.Equal(302, gated.Status);
			Assert.StartsWith("/profile/two_factor_auth?", gated.Location);
		}
	}

	// wrong recovery codes count toward the lockout, and a locked account takes no recovery code
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task RecoveryCodesFollowTheLockout(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(_, List<string> codes) = await SetUp(probe, "locked-codes@example.org");

		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled());
		Assert.Equal(302, (await PasswordPagesTests.SignIn(browser, "locked-codes@example.org", "the right password")).Status);
		for (int attempt = 1; attempt <= 5; attempt++)
		{
			Assert.Equal(401, (await SecondFactor(browser, "recovery_code", "wrong-code-" + attempt)).Status);
		}
		Assert.NotNull((await PasswordPagesTests.UserByEmail(probe, "locked-codes@example.org"))!.LockoutEndAt);

		Assert.Equal(401, (await SecondFactor(browser, "recovery_code", codes[0])).Status);
		Assert.False(browser.SignedIn);
		await using AppDbContext db = probe.Open();
		User user = (await PasswordPagesTests.UserByEmail(probe, "locked-codes@example.org"))!;
		Assert.Equal(10, await db.UserRecoveryCodes.CountAsync(c => c.UserID == user.ID && c.UsedAt == null));
	}
}
