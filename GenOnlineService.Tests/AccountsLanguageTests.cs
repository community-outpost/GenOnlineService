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
using GenOnlineService.Mail;
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// The sign-in pages, the account page and the account mails in the player's language (AccountsText): the language switch's
// cookie, then the browser's Accept-Language, then English; a word a language lacks is English; the staff pages stay English
public class AccountsLanguageTests
{
	private static readonly DateTime s_day = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

	[Theory]
	[InlineData("de-AT", "de")]
	[InlineData("ar-EG", "ar")]
	[InlineData("ru", "ru")]
	[InlineData("en-GB", "en")]
	[InlineData("fr-FR", "en")]
	[InlineData("", "en")]
	public void ACultureResolvesToItsLanguageOrEnglish(string culture, string expected)
	{
		Assert.Equal(expected, AccountsText.Resolve(CultureInfo.GetCultureInfo(culture)).Name);
	}

	[Fact]
	public void NoCultureIsEnglish()
	{
		Assert.Equal("en", AccountsText.Resolve(null).Name);
		Assert.Equal("Sign in", AccountsText.Get("SignIn.Title"));
	}

	// "Brand" is in the English resource only: each other language falls back to it rather than showing a gap or the key
	[Theory]
	[InlineData("de")]
	[InlineData("ru")]
	[InlineData("ar")]
	public void AWordALanguageLacksIsEnglish(string language)
	{
		CultureInfo culture = CultureInfo.GetCultureInfo(language);
		Assert.Null(AccountsText.Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString("Brand"));
		Assert.Equal("Generals Online", AccountsText.Get("Brand", culture));
	}

	// Every word of the English (neutral) resource but the brand is in each language, with the same placeholders, so a translation
	// never drops a number or a name from a sentence
	[Theory]
	[InlineData("de")]
	public void EachLanguageHasEveryWordWithItsPlaceholders(string language)
	{
		System.Resources.ResourceSet english = AccountsText.Resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
		System.Resources.ResourceSet translated = AccountsText.Resources.GetResourceSet(CultureInfo.GetCultureInfo(language), createIfNotExists: true, tryParents: false)
			?? throw new Xunit.Sdk.XunitException($"no {language} resources");
		var englishKeys = english.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).Where(k => k != "Brand").ToHashSet();
		var translatedKeys = translated.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
		Assert.Empty(englishKeys.Except(translatedKeys));
		Assert.Empty(translatedKeys.Except(englishKeys));
		foreach (string key in englishKeys)
		{
			string value = translated.GetString(key)!;
			Assert.False(String.IsNullOrWhiteSpace(value), $"{language} {key} is empty");
			Assert.Equal(Placeholders(english.GetString(key)!), Placeholders(value));
		}
	}

	private static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

	// Each language's own mail: its subject, body and footer, in HTML and text, with the values filled in
	[Theory]
	[InlineData("de", "Bestätige deine E-Mail-Adresse", "bitte bestätige, dass diese Adresse dir gehört", "wegen deines Generals-Online-Kontos")]
	public void MailsAreInThePlayersLanguage(string language, string subject, string body, string footer)
	{
		var values = new Dictionary<string, string> { ["name"] = "Nelly#0042", ["link"] = "https://accounts.test/confirm?t=1&x=2" };
		EmailMessage mail = EmailTemplates.Render(EmailTemplates.Confirmation, "player@example.org", values, CultureInfo.GetCultureInfo(language));
		Assert.Equal(subject, mail.Subject);
		Assert.Contains(body, mail.Text);
		Assert.Contains(footer, mail.Text);
		Assert.Contains(footer, System.Net.WebUtility.HtmlDecode(mail.Html));
		Assert.Contains("https://accounts.test/confirm?t=1&amp;x=2", mail.Html);
		Assert.Contains($"lang=\"{language}\"", mail.Html);

		EmailMessage reset = EmailTemplates.Render(EmailTemplates.PasswordReset, "player@example.org", values, CultureInfo.GetCultureInfo(language));
		Assert.Equal(AccountsText.Get("Mail.PasswordReset.Subject", CultureInfo.GetCultureInfo(language)), reset.Subject);
		Assert.Contains("Nelly#0042", reset.Text);
		Assert.Contains(footer, reset.Text);
	}

	[Fact]
	public void AnUnknownWordIsAnError()
	{
		Assert.Throws<KeyNotFoundException>(() => AccountsText.Get("No.Such.Key", CultureInfo.GetCultureInfo("de")));
	}

	[Theory]
	[InlineData("en", "2026-11-17")]
	[InlineData("de", "17.11.2026")]
	[InlineData("ru", "17.11.2026")]
	public void DaysAreInThePagesLanguage(string language, string expected)
	{
		Assert.Equal(expected, AccountsText.Day(s_day, CultureInfo.GetCultureInfo(language)));
	}

	// Arabic's default calendar is Umm al-Qura (1448); the account page shows the Gregorian day
	[Fact]
	public void ArabicDaysAreGregorian()
	{
		string day = AccountsText.Day(s_day, CultureInfo.GetCultureInfo("ar"));
		Assert.Contains("2026", day);
		Assert.Contains("17", day);
		Assert.DoesNotContain("1448", day);
	}

	// PortalSetup's refusals for the staff pages pass no language: English, left to right, with no switch, whatever the
	// thread's culture
	[Fact]
	public void TheStaffPagesStayEnglish()
	{
		CultureInfo culture = CultureInfo.CurrentCulture, uiCulture = CultureInfo.CurrentUICulture;
		try
		{
			CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar");
			string page = PortalLook.Document("Not allowed", "<p>b</p>");
			Assert.Contains("<html lang=\"en\" dir=\"ltr\" class=\"dark\">", page);
			Assert.Contains("<title>Not allowed · Generals Online</title>", page);
			Assert.DoesNotContain("go-languages", page);
		}
		finally
		{
			CultureInfo.CurrentCulture = culture;
			CultureInfo.CurrentUICulture = uiCulture;
		}
	}

	[Fact]
	public void TheSwitchNamesEachLanguageInItsOwnWordsAndMarksTheCurrentOne()
	{
		string nav = PortalLook.LanguageSwitch(new PortalLanguage(CultureInfo.GetCultureInfo("ru"), "/users/sign_in?ReturnUrl=%2Fprofile"));
		Assert.Equal(["English", "Deutsch", "Русский", "العربية"], Regex.Matches(nav, ">([^<]+)</a>").Select(m => m.Groups[1].Value));
		Assert.Single(Regex.Matches(nav, "aria-current=\"true\""));
		Assert.Contains("lang=\"ru\" hreflang=\"ru\" class=\"go-language\" aria-current=\"true\"", nav);
		Assert.Contains("href=\"/users/language?culture=de&amp;ReturnUrl=%2Fusers%2Fsign_in%3FReturnUrl%3D%252Fprofile\"", nav);
	}

	[Fact]
	public void AMailInALanguageWithoutItsOwnCopyIsEnglish()
	{
		var values = new Dictionary<string, string> { ["name"] = "Player", ["link"] = "https://accounts.test/confirm?t=1" };
		EmailMessage mail = EmailTemplates.Render(EmailTemplates.Confirmation, "player@example.org", values, CultureInfo.GetCultureInfo("fr"));
		Assert.Equal("Confirm your email address", mail.Subject);
		Assert.Contains("please confirm that this address is yours", mail.Text);
	}

	private static AccountsBrowser Browser(ProbeDatabase probe) => new(probe, PasswordPagesTests.Enabled());

	// the page's language and direction, as "ar/rtl"
	private static string Lang(PageResponse page)
	{
		System.Text.RegularExpressions.Match match = Regex.Match(page.Body, "<html lang=\"([^\"]+)\" dir=\"([^\"]+)\"");
		return match.Success ? $"{match.Groups[1].Value}/{match.Groups[2].Value}" : "none";
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task WithoutAChoiceThePagesAreEnglish(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		AccountsBrowser browser = Browser(probe);

		PageResponse page = await browser.Get("/users/sign_in");
		Assert.Equal(200, page.Status);
		Assert.Equal("en/ltr", Lang(page));
		Assert.Contains("<nav class=\"go-languages\"", page.Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheBrowsersLanguagePicksThePage(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		AccountsBrowser browser = Browser(probe);

		browser.Headers["Accept-Language"] = "de-AT,de;q=0.9,en;q=0.5";
		PageResponse german = await browser.Get("/users/sign_in");
		Assert.Equal("de/ltr", Lang(german));
		Assert.Contains("<title>Anmelden · Generals Online</title>", german.Body);
		Assert.Contains(">Noch kein Konto? <a", german.Body);

		// a language the pages do not have is English
		browser.Headers["Accept-Language"] = "fr-FR";
		Assert.Equal("en/ltr", Lang(await browser.Get("/users/sign_in")));

		// the browser's next choice the pages have, by preference
		browser.Headers["Accept-Language"] = "fr-FR, ar;q=0.5, ru;q=0.8";
		Assert.Equal("ru/ltr", Lang(await browser.Get("/users/sign_in")));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheSwitchRemembersTheLanguageOverTheBrowsers(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		AccountsBrowser browser = Browser(probe);
		browser.Headers["Accept-Language"] = "de-DE";

		PageResponse switched = await browser.Get("/users/language?culture=ar&ReturnUrl=%2Fusers%2Fsign_up");
		Assert.Equal(302, switched.Status);
		Assert.Equal("/users/sign_up", switched.Location);
		string cookie = Assert.Single(switched.SetCookies, c => c.StartsWith(AccountsText.CookieName + "=", StringComparison.Ordinal));
		Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

		// Arabic reads right to left, over the browser's German
		PageResponse page = await browser.Get("/users/sign_up");
		Assert.Equal("ar/rtl", Lang(page));
		Assert.Contains("lang=\"ar\" hreflang=\"ar\" class=\"go-language\" aria-current=\"true\"", page.Body);

		// a refused form comes back in the same language
		PageResponse refused = await browser.Post("/users/sign_up", new()
		{
			["__go_consent_token"] = AccountsBrowser.Token(page),
			["display_name"] = "Rtl",
			["email"] = "not an address",
			["password"] = "a long enough password",
			["ReturnUrl"] = "/profile"
		});
		Assert.Equal(400, refused.Status);
		Assert.Equal("ar/rtl", Lang(refused));
		// and its switch goes back to the form, not to the answer of a POST
		Assert.Contains("ReturnUrl=%2Fusers%2Fsign_up%3FReturnUrl%3D%252Fprofile\"", refused.Body);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheSwitchRefusesUnknownLanguagesAndForeignAddresses(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		AccountsBrowser browser = Browser(probe);

		PageResponse unknown = await browser.Get("/users/language?culture=xx&ReturnUrl=%2Fusers%2Fsign_in");
		Assert.Equal(302, unknown.Status);
		Assert.Equal("/users/sign_in", unknown.Location);
		Assert.Null(browser.Cookie(AccountsText.CookieName));

		PageResponse foreign = await browser.Get("/users/language?culture=de&ReturnUrl=https%3A%2F%2Fevil.example%2F");
		Assert.Equal(302, foreign.Status);
		Assert.Equal(AccountsController.AccountPath, foreign.Location);
		Assert.NotNull(browser.Cookie(AccountsText.CookieName));
	}
}
