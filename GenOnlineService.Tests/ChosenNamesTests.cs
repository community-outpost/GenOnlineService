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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Xunit;

namespace GenOnlineService.Tests;

// Players choose their own display name (identity hub T13, S17): a provider sign-up owes one until the player picks it
public class ChosenNamesTests
{
	internal static UpstreamProviderSettings Discord() => AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
	{
		["Accounts:ephemeral_keys"] = "true",
		["Accounts:providers:discord:enabled"] = "true",
		["Accounts:providers:discord:client_id"] = "d-id",
		["Accounts:providers:discord:client_secret"] = "d-secret"
	}).Build()).EnabledProviders.Single(p => p.Name == "discord");

	internal static ClaimsPrincipal DiscordUser(string id, string name) =>
		new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, name) }, "test"));

	internal static async Task<User> ProviderSignUp(ProbeDatabase probe, string id, string name)
	{
		UpstreamSignInResult result = await new UpstreamAccounts(new ProbeFactory(probe)).SignIn(Discord(), DiscordUser(id, name));
		Assert.Null(result.Error);
		await using AppDbContext db = probe.Open();
		return await db.Users.SingleAsync(u => u.ID == result.User!.ID);
	}

	// a new provider account owes a name; signing in again does not settle it
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AProviderSignUpOwesAName(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		User created = await ProviderSignUp(probe, "80351110224678912", "nelly");
		Assert.NotNull(created.NameRequiredAt);

		User again = await ProviderSignUp(probe, "80351110224678912", "nelly");
		Assert.Equal(created.ID, again.ID);
		Assert.Equal(created.NameRequiredAt, again.NameRequiredAt);
	}

	// a password sign-up types the name on the form, so it owes nothing
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task APasswordSignUpOwesNoName(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		Assert.Equal(302, (await PasswordPagesTests.SignUp(new AccountsBrowser(probe, PasswordPagesTests.Enabled()), "Typed", "typed@example.org", "a long enough password")).Status);
		Assert.Null((await PasswordPagesTests.UserByEmail(probe, "typed@example.org"))!.NameRequiredAt);
	}

	// the new account is named by a neutral placeholder; the provider's name comes back only as a suggestion, never stored
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AProviderNameIsOnlyASuggestion(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;

		var accounts = new UpstreamAccounts(new ProbeFactory(probe));
		UpstreamSignInResult first = await accounts.SignIn(Discord(), DiscordUser("80351110224678912", "nelly"));
		Assert.Matches("^Player[0-9]{6}$", first.User!.Username);
		Assert.Equal("nelly", first.SuggestedName);

		// still owed on the next sign-in, so it is suggested again
		Assert.Equal("nelly", (await accounts.SignIn(Discord(), DiscordUser("80351110224678912", "nelly"))).SuggestedName);

		await using AppDbContext db = probe.Open();
		Assert.False(await db.Users.AnyAsync(u => u.Username == "nelly"));
		Assert.False(await db.UserUsernameHistory.AnyAsync(h => h.Username == "nelly"));
	}

	private const string AuthorizeQuery = "?client_id=genonline_zerohour&response_type=code&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcallback&scope=openid%20game&state=s&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";

	private static Dictionary<string, string?> WithGameClient() => PasswordPagesTests.Enabled(
		("Accounts:clients:0:client_id", "genonline_zerohour"),
		("Accounts:clients:0:display_name", "Generals Online (Zero Hour)"),
		("Accounts:clients:0:kind", "game"),
		("Accounts:clients:0:game", "zerohour"),
		("Accounts:clients:0:redirect_uris:0", "http://127.0.0.1/callback"));

	// a signed-in browser whose account owes a name, as a provider sign-up leaves it
	private static async Task<(AccountsBrowser Browser, User User)> OwingPlayer(ProbeDatabase probe, string email)
	{
		var browser = new AccountsBrowser(probe, WithGameClient());
		await AccountsSetup.SeedClients(browser.Services, browser.Settings);
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Placeholder", email, "a long enough password")).Status);
		await using (AppDbContext db = probe.Open())
		{
			await ClientRegistry.Reload(db);
			User owing = await db.Users.AsTracking().SingleAsync(u => u.Email == email);
			owing.NameRequiredAt = DateTime.UtcNow;
			await db.SaveChangesAsync();
		}
		return (browser, (await PasswordPagesTests.UserByEmail(probe, email))!);
	}

	private static async Task<PageResponse> Choose(AccountsBrowser browser, string name)
	{
		PageResponse page = await browser.Get("/users/sign_up/welcome?ReturnUrl=" + Uri.EscapeDataString("/oauth/authorize" + AuthorizeQuery));
		Assert.Equal(200, page.Status);
		return await browser.Post("/users/sign_up/welcome", new()
		{
			["__go_consent_token"] = AccountsBrowser.Token(page),
			["display_name"] = name,
			["ReturnUrl"] = "/oauth/authorize" + AuthorizeQuery
		});
	}

	// no token before the name: authorize sends the player to choose it, and a silent request is told to ask
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AuthorizeAsksForTheNameFirst(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await OwingPlayer(probe, "owing@example.org");

		PageResponse gated = await browser.Get("/oauth/authorize" + AuthorizeQuery);
		Assert.Equal(302, gated.Status);
		Assert.StartsWith("/users/sign_up/welcome?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dgenonline_zerohour", gated.Location);

		PageResponse silent = await browser.Get("/oauth/authorize" + AuthorizeQuery + "&prompt=none");
		Assert.Contains("error=interaction_required", silent.Location);
	}

	// the suggestion fills the box for its own account only; the chosen name settles the debt and authorize goes on
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheWelcomePageChoosesTheName(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await OwingPlayer(probe, "chooser@example.org");
		var protection = browser.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();

		browser.SetCookie(NameSuggestion.CookieName, NameSuggestion.Protect(protection, user.ID + 1, "someone else"));
		Assert.DoesNotContain("someone else", (await browser.Get("/users/sign_up/welcome")).Body);
		browser.SetCookie(NameSuggestion.CookieName, NameSuggestion.Protect(protection, user.ID, "nelly"));
		Assert.Contains("value=\"nelly\"", (await browser.Get("/users/sign_up/welcome")).Body);

		PageResponse tooShort = await Choose(browser, "ab");
		Assert.Equal(400, tooShort.Status);
		Assert.Contains("Names must be 3 to 16 characters.", tooShort.Body);
		Assert.NotNull((await PasswordPagesTests.UserByEmail(probe, "chooser@example.org"))!.NameRequiredAt);

		PageResponse chosen = await Choose(browser, "Nelly");
		Assert.Equal(302, chosen.Status);
		Assert.Equal("/oauth/authorize" + AuthorizeQuery, chosen.Location);
		// the suggestion is dropped once the name is chosen
		Assert.Contains(chosen.SetCookies, c => c.StartsWith(NameSuggestion.CookieName + "=;", StringComparison.Ordinal));

		User after = (await PasswordPagesTests.UserByEmail(probe, "chooser@example.org"))!;
		Assert.Null(after.NameRequiredAt);
		Assert.Equal("Nelly", after.Username);
		await using (AppDbContext db = probe.Open())
		{
			Assert.True(await db.UserUsernameHistory.AnyAsync(h => h.UserID == user.ID && h.Username == "Placeholder" && h.ChangedBy == $"user:{user.ID}"));
		}

		PageResponse passed = await browser.Get("/oauth/authorize" + AuthorizeQuery);
		Assert.StartsWith("http://127.0.0.1/callback?code=", passed.Location);

		// nothing is owed any more: the page sends the player on
		PageResponse done = await browser.Get("/users/sign_up/welcome?ReturnUrl=%2Fdone");
		Assert.Equal(302, done.Status);
		Assert.Equal("/done", done.Location);
	}

	// another player already holds the name with this tag: the chosen name draws a free tag instead of failing
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AChosenNameDrawsAFreeTagWhenItsOwnIsTaken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Assert.Equal(302, (await PasswordPagesTests.SignUp(new AccountsBrowser(probe, PasswordPagesTests.Enabled()), "Nelly", "holder@example.org", "a long enough password")).Status);
		(AccountsBrowser browser, User user) = await OwingPlayer(probe, "late@example.org");
		int holderTag = (await PasswordPagesTests.UserByEmail(probe, "holder@example.org"))!.NameTag;
		await using (AppDbContext db = probe.Open())
		{
			User late = await db.Users.AsTracking().SingleAsync(u => u.ID == user.ID);
			late.NameTag = holderTag;
			await db.SaveChangesAsync();
		}

		Assert.Equal(302, (await Choose(browser, "Nelly")).Status);
		User after = (await PasswordPagesTests.UserByEmail(probe, "late@example.org"))!;
		Assert.Equal("Nelly", after.Username);
		Assert.NotEqual(holderTag, after.NameTag);
		Assert.Null(after.NameRequiredAt);
	}

	// a rename through the API (PATCH /api/v1/me) is a choice too
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ASelfRenameSettlesTheName(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		User created = await ProviderSignUp(probe, "80351110224678913", "renamer");

		Assert.True((await new Names.PlayerNames(new ProbeFactory(probe)).RenameSelfAsync(created.ID, "Renamer")).Ok);
		await using AppDbContext db = probe.Open();
		Assert.Null((await db.Users.SingleAsync(u => u.ID == created.ID)).NameRequiredAt);
	}
}
