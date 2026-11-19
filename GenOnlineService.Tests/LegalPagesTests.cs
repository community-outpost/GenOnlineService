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
using Microsoft.Extensions.Configuration;
using System.Net;
using Xunit;

namespace GenOnlineService.Tests;

// The built-in Rules of Engagement, Terms of Service and Privacy Policy (/legal/*): served by this service on the design
// system to anyone, and linked wherever a document has no Agreements:<doc>:url of its own
[Collection(AdminAreaCollection.Name)]
public class LegalPagesTests
{
	private static readonly string[] s_cards = ["respect", "chat", "fair_play", "match", "accounts", "service"];

	private static AgreementsSettings Read(params (string Key, string Value)[] values)
		=> AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build()).Agreements;

	// each page answers without a sign-in, on the design system, in English, with no draft marks or notes left in it
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EachPageAnswersAnonymouslyOnTheDesignSystem(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, new Dictionary<string, string?>());

		// support answers players, security@ takes reports and legal@ notices; privacy@ takes data requests, on the privacy page and for account deletion in the terms
		foreach ((string path, string title, string contacts) in new[] { (LegalPages.RulesPath, "Rules of Engagement", "support@generals.online|security@generals.online"), (LegalPages.TermsPath, "Terms of Service", "support@generals.online|legal@generals.online|privacy@generals.online"), (LegalPages.PrivacyPath, "Privacy Policy", "privacy@generals.online") })
		{
			PageResponse page = await browser.Get(path);
			Assert.Equal(200, page.Status);
			Assert.StartsWith("<!doctype html><html lang=\"en\" dir=\"ltr\">", page.Body);
			Assert.Contains($"href=\"{PortalAssets.Url(PortalLook.TokensPath)}\"", page.Body);
			Assert.Contains($">{title}</h1>", page.Body);
			Assert.Contains("<footer class=\"gp-footer\">", page.Body);
			Assert.Contains("class=\"gp-lang\"", page.Body);
			Assert.Contains("<div class=\"gp-doc\" lang=\"en\" dir=\"ltr\">", page.Body);
			foreach (string contact in contacts.Split('|'))
			{
				Assert.Contains(contact, page.Body);
			}
			Assert.DoesNotContain("draft", page.Body, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("TODO", page.Body);
			Assert.DoesNotContain("<!--", page.Body);
			Assert.DoesNotContain("GenOnline", page.Body);
			PortalLookTests.AssertGpClassesDefined(page.Body);
			PortalLookTests.AssertSelfContained(page.Body);
		}

		// the content stays English and left to right inside an Arabic page
		Assert.Equal(302, (await browser.Get("/users/language?culture=ar&ReturnUrl=%2Flegal%2Fterms")).Status);
		PageResponse arabic = await browser.Get(LegalPages.TermsPath);
		Assert.StartsWith("<!doctype html><html lang=\"ar\" dir=\"rtl\">", arabic.Body);
		Assert.Contains("<div class=\"gp-doc\" lang=\"en\" dir=\"ltr\">", arabic.Body);
	}

	// the rules expand each card of the sign-in, under an anchor of the card's id; the version and day show when configured
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheRulesHaveAnAnchorPerCardAndTheVersionInForce(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		PageResponse plain = await new AccountsBrowser(probe, new Dictionary<string, string?>()).Get(LegalPages.RulesPath);
		foreach (string card in s_cards)
		{
			Assert.Contains($"<section id=\"{card}\"", plain.Body);
		}
		Assert.Contains("No doxxing", plain.Body);
		Assert.Contains("Found a security problem? Tell us privately.", plain.Body);
		Assert.DoesNotContain("Version ", plain.Body);

		var versioned = new AccountsBrowser(probe, new Dictionary<string, string?> { ["Agreements:rules:version"] = "2026-11", ["Agreements:rules:date"] = "2026-11-18", ["Agreements:terms:version"] = "3" });
		PageResponse rules = await versioned.Get(LegalPages.RulesPath);
		Assert.Contains("Version 2026-11, in effect from 18 November 2026", rules.Body);
		PageResponse terms = await versioned.Get(LegalPages.TermsPath);
		Assert.Contains("Version 3", terms.Body);
		Assert.DoesNotContain("in effect from", terms.Body);
	}

	// the footer links the built-in page of each document without an address, and the configured address otherwise
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheFooterLinksTheBuiltInPagesUnlessAnAddressIsConfigured(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		PageResponse plain = await new AccountsBrowser(probe, PasswordPagesTests.Enabled()).Get("/users/sign_in");
		Assert.Equal(200, plain.Status);
		Assert.Contains("<a href=\"/legal/terms\" target=\"_blank\" rel=\"noopener noreferrer\">Terms of Service</a>", plain.Body);
		Assert.Contains("<a href=\"/legal/privacy\" target=\"_blank\" rel=\"noopener noreferrer\">Privacy Policy</a>", plain.Body);
		Assert.Contains("<a href=\"/legal/rules\" target=\"_blank\" rel=\"noopener noreferrer\">Rules of Engagement</a>", plain.Body);

		PageResponse configured = await new AccountsBrowser(probe, PasswordPagesTests.Enabled(("Agreements:terms:url", AgreementsTests.TermsUrl))).Get("/users/sign_in");
		Assert.Contains($"<a href=\"{AgreementsTests.TermsUrl}\" target=\"_blank\" rel=\"noopener noreferrer\">Terms of Service</a>", configured.Body);
		Assert.DoesNotContain("href=\"/legal/terms\"", configured.Body);
		Assert.Contains("href=\"/legal/privacy\"", configured.Body);
		Assert.Contains("href=\"/legal/rules\"", configured.Body);
	}

	// a version without an address starts: the built-in page is the address, on Accounts:issuer when it is set
	[Fact]
	public void AVersionWithoutAnAddressUsesTheBuiltInPage()
	{
		AgreementsSettings settings = Read(("Agreements:rules:version", "1"), ("Agreements:terms:version", "2"));
		Assert.Equal(new[] { AgreementsSettings.Rules, AgreementsSettings.Terms }, settings.Required.Select(d => d.Name));
		Assert.Equal("/legal/rules", settings.RulesDocument.Url);
		Assert.True(settings.RulesDocument.BuiltIn);
		Assert.Equal("/legal/terms", settings.TermsDocument.Url);
		Assert.Equal("/legal/privacy", settings.PrivacyUrl);

		AgreementsSettings hosted = Read(("Accounts:issuer", "https://login.example/"), ("Agreements:rules:version", "1"), ("Agreements:privacy:url", AgreementsTests.PrivacyUrl));
		Assert.Equal("https://login.example/legal/rules", hosted.RulesDocument.Url);
		Assert.Equal("https://login.example/legal/terms", hosted.TermsDocument.Url);
		Assert.Equal(AgreementsTests.PrivacyUrl, hosted.PrivacyUrl);

		AgreementsSettings linked = Read(("Agreements:rules:version", "1"), ("Agreements:rules:url", AgreementsTests.RulesUrl));
		Assert.Equal(AgreementsTests.RulesUrl, linked.RulesDocument.Url);
		Assert.False(linked.RulesDocument.BuiltIn);
	}

	// the other checks still stop the start without an address
	[Fact]
	public void ABadDateOrVersionStillStopsTheStartWithoutAnAddress()
	{
		Assert.Equal("Agreements:rules:date must be a day as yyyy-MM-dd, such as 2026-11-18.",
			Assert.Throws<InvalidOperationException>(() => Read(("Agreements:rules:version", "1"), ("Agreements:rules:date", "18.11.2026"))).Message);
		Assert.Contains("Agreements:terms:version must be at most",
			Assert.Throws<InvalidOperationException>(() => Read(("Agreements:terms:version", new string('x', AgreementDocument.VersionMaxLength + 1)))).Message);
	}

	// a version without an address gates the sign-in as before; each card's "Read the full rules" opens its own section
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AVersionWithoutAnAddressGatesWithTheBuiltInLinks(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await AgreementGateTests.SignedUp(probe, "Builtin", ("Agreements:rules:version", "2026-11"), ("Agreements:terms:version", "1"));
		const string issuer = "http://accounts.test";

		PageResponse gated = await browser.Get("/oauth/authorize" + AgreementGateTests.AuthorizeQuery);
		Assert.StartsWith(AgreementGateTests.GatedPrefix, gated.Location);

		// every rule on one page, whose link goes to the built-in rules
		PageResponse page = await browser.Get(gated.Location!);
		Assert.Equal(200, page.Status);
		Assert.Contains($"<a href=\"{issuer}/legal/rules\" target=\"_blank\" rel=\"noopener noreferrer\">Read the full rules</a>", page.Body);
		Dictionary<string, string> fields = new();
		foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(page.Body, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">"))
		{
			fields[WebUtility.HtmlDecode(match.Groups[1].Value)] = WebUtility.HtmlDecode(match.Groups[2].Value);
		}
		PageResponse agreed = await browser.Post("/users/agreements", fields);
		Assert.Equal(302, agreed.Status);
		page = await browser.Get(agreed.Location!);

		Assert.Contains("I agree to the Terms of Service", page.Body);
		Assert.Contains($"href=\"{issuer}/legal/terms\"", page.Body);
		Assert.Contains($"href=\"{issuer}/legal/privacy\"", page.Body);

		PageResponse profile = await browser.Get("/profile");
		Assert.Contains($"href=\"{issuer}/legal/rules\"", profile.Body);
		Assert.Contains($"href=\"{issuer}/legal/privacy\"", profile.Body);
	}
}
