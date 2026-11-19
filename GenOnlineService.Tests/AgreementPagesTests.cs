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
using Xunit;

namespace GenOnlineService.Tests;

// The agreement pages (identity hub T18c, S19): first sign-in is choose name, then the rule cards one by one, then the
// Terms with a tick box, then the game; only what is owed is shown, and nothing is recorded before the last step
[Collection(AdminAreaCollection.Name)]
public class AgreementPagesTests
{
	private const string Authorize = "/oauth/authorize" + AgreementGateTests.AuthorizeQuery;
	private const string Wizard = "/users/agreements?ReturnUrl=" + "%2Foauth%2Fauthorize%3Fclient_id%3Dgenonline_zerohour%26response_type%3Dcode%26redirect_uri%3Dhttp%253A%252F%252F127.0.0.1%252Fcallback%26scope%3Dopenid%2520game%26state%3Ds%26code_challenge%3DE9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM%26code_challenge_method%3DS256";

	private static readonly string[] s_cardTitles = ["Respect", "Chat, names and clan tags", "Fair play", "Match behaviour", "Accounts", "The service"];

	// Agrees to the card a page shows, as its Agree button does
	private static Task<PageResponse> AgreeCard(AccountsBrowser browser, PageResponse page)
		=> browser.Post("/users/agreements", HiddenFields(page));

	// Every hidden field of the page's form, as a browser posts them back
	private static Dictionary<string, string> HiddenFields(PageResponse page)
	{
		var fields = new Dictionary<string, string>();
		foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(page.Body, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">"))
		{
			fields[WebUtility.HtmlDecode(match.Groups[1].Value)] = WebUtility.HtmlDecode(match.Groups[2].Value);
		}
		Assert.True(fields.Count > 0, "the page has no form: " + page.Body);
		return fields;
	}

	// The sign-in of a new player end to end: the name first, then all the rules on one page, then the Terms, then the code
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AFirstSignInGoesNameRulesTermsThenPlays(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await AgreementGateTests.SignedUp(probe, "Placeholder", AgreementsTests.Configured());
		await using (AppDbContext db = probe.Open())
		{
			await db.Users.Where(u => u.ID == user.ID).ExecuteUpdateAsync(set => set.SetProperty(u => u.NameRequiredAt, DateTime.UtcNow));
		}

		// the name
		PageResponse welcome = await browser.Get((await browser.Get(Authorize)).Location!);
		Assert.Equal(200, welcome.Status);
		PageResponse named = await browser.Post("/users/sign_up/welcome", new()
		{
			["__go_consent_token"] = AccountsBrowser.Token(welcome),
			["display_name"] = "Newcomer",
			["ReturnUrl"] = Authorize
		});
		Assert.Equal(Authorize, named.Location);

		// then the agreements
		PageResponse gated = await browser.Get(Authorize);
		Assert.Equal(Wizard, gated.Location);

		// every rule on one page, with one Agree
		PageResponse page = await browser.Get(gated.Location!);
		Assert.Equal(200, page.Status);
		Assert.Contains("<h1 class=\"gp-title\">The rules</h1>", page.Body);
		foreach (string title in s_cardTitles)
		{
			Assert.Contains($"<h2 class=\"gp-rule-title\">", page.Body);
			Assert.Contains($"<span>{WebUtility.HtmlEncode(title)}</span>", page.Body);
		}
		Assert.Equal(s_cardTitles.Length, System.Text.RegularExpressions.Regex.Matches(page.Body, "<h2 class=\"gp-rule-title\">").Count);
		Assert.Contains("Treat every player with respect, whether they are your teammate, your opponent or new to the game.", page.Body);
		Assert.Contains("No doxxing.", page.Body);
		Assert.Contains("Found a security problem? Tell us privately.", page.Body);
		Assert.DoesNotContain("Rule 1 of 6", page.Body);
		Assert.Contains($"href=\"{AgreementsTests.RulesUrl}\"", page.Body);
		Assert.Contains("Read the full rules", page.Body);
		Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(page.Body, ">Agree</button>").Count);
		Assert.DoesNotContain("type=\"checkbox\"", page.Body);
		Assert.Matches("<button type=\"submit\" class=\"gp-btn gp-btn-primary gp-btn-lg gp-btn-fill\">Agree</button>", page.Body);
		// a first agreement shows no change notice
		Assert.DoesNotContain("The rules changed", page.Body);

		PageResponse agreed = await AgreeCard(browser, page);
		Assert.Equal(302, agreed.Status);
		Assert.Contains("step=terms", agreed.Location);
		page = await browser.Get(agreed.Location!);

		// nothing is recorded by the rules page alone
		await using (AppDbContext db = probe.Open())
		{
			Assert.False(await db.UserAgreements.AnyAsync(a => a.UserID == user.ID));
		}

		// the Terms step
		Assert.Equal(200, page.Status);
		Assert.Contains("I agree to the Terms of Service", page.Body);
		Assert.Contains($"href=\"{AgreementsTests.TermsUrl}\"", page.Body);
		Assert.Contains($"href=\"{AgreementsTests.PrivacyUrl}\"", page.Body);
		Assert.Contains("Start playing", page.Body);

		// without the tick nothing is recorded
		Dictionary<string, string> unticked = HiddenFields(page);
		PageResponse refused = await browser.Post("/users/agreements", unticked);
		Assert.Equal(400, refused.Status);
		Assert.Contains("Tick the box to agree to the Terms of Service.", refused.Body);
		await using (AppDbContext db = probe.Open())
		{
			Assert.False(await db.UserAgreements.AnyAsync(a => a.UserID == user.ID));
		}

		Dictionary<string, string> ticked = HiddenFields(page);
		ticked["agree_terms"] = "1";
		PageResponse started = await browser.Post("/users/agreements", ticked);
		Assert.Equal(302, started.Status);
		Assert.Equal(Authorize, started.Location);
		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get(Authorize)).Location);

		await using (AppDbContext db = probe.Open())
		{
			var rows = await db.UserAgreements.Where(a => a.UserID == user.ID).OrderBy(a => a.Document).Select(a => new { a.Document, a.Version }).ToListAsync();
			Assert.Equal(new[] { ("rules", "2026-11"), ("terms", "1") }, rows.Select(r => (r.Document, r.Version)));
			var audits = await db.AuditLog.Where(a => a.Action == Agreements.AcceptedAction && a.TargetID == user.ID).ToListAsync();
			Assert.Equal(2, audits.Count);
			Assert.All(audits, a => Assert.Equal("web", a.Source));
		}

		// nothing owed any more: the page sends the player on
		PageResponse done = await browser.Get("/users/agreements?ReturnUrl=%2Fdone");
		Assert.Equal(302, done.Status);
		Assert.Equal("/done", done.Location);
	}

	// a new rules version asks again at the next sign-in and says when the rules changed; the Terms, still current, are skipped
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ANewVersionAsksAgainWithItsDate(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(_, User user) = await AgreementGateTests.SignedUp(probe, "Veteran", AgreementsTests.Configured());
		await AgreementGateTests.Agree(probe, user.ID, "rules", "2026-11");
		await AgreementGateTests.Agree(probe, user.ID, "terms", "1");

		(string Key, string Value)[] bumped = AgreementsTests.Configured(rules: "2026-12").Select(s => s.Key == "Agreements:rules:date" ? (s.Key, "2026-12-01") : s).ToArray();
		AccountsBrowser browser = await AgreementGateTests.Browser(probe, bumped);
		Assert.Equal(302, (await PasswordPagesTests.SignIn(browser, "veteran@example.org", "a long enough password")).Status);

		PageResponse gated = await browser.Get(Authorize);
		Assert.Equal(Wizard, gated.Location);
		PageResponse page = await browser.Get(gated.Location!);
		Assert.Contains("The rules changed on 2026-12-01.", page.Body);
		Assert.Contains("Respect", page.Body);

		// no Terms step: the one rules page records the rules and goes back to the sign-in
		PageResponse agreedAgain = await AgreeCard(browser, page);
		Assert.Equal(302, agreedAgain.Status);
		Assert.Equal(Authorize, agreedAgain.Location);
		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get(Authorize)).Location);
		await using AppDbContext db = probe.Open();
		Assert.True(await db.UserAgreements.AnyAsync(a => a.UserID == user.ID && a.Document == "rules" && a.Version == "2026-12"));
	}

	// only the Terms owed: no card, straight to the tick box
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task OnlyTheTermsOwedSkipsTheCards(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await AgreementGateTests.SignedUp(probe, "Termer", AgreementsTests.Configured());
		await AgreementGateTests.Agree(probe, user.ID, "rules", "2026-11");

		PageResponse page = await browser.Get(Wizard);
		Assert.Equal(200, page.Status);
		Assert.DoesNotContain("Rule 1 of 6", page.Body);
		Assert.Contains("I agree to the Terms of Service", page.Body);

		Dictionary<string, string> ticked = HiddenFields(page);
		ticked["agree_terms"] = "1";
		Assert.Equal(Authorize, (await browser.Post("/users/agreements", ticked)).Location);
		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get(Authorize)).Location);
	}

	// a page of the earlier card-by-card flow, open during a deploy, posts step=card: it still agrees to the rules
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task APageOfTheOldCardFlowStillAgrees(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await AgreementGateTests.SignedUp(probe, "Oldpage", AgreementsTests.Configured());
		await AgreementGateTests.Agree(probe, user.ID, "terms", "1");

		PageResponse page = await browser.Get(Wizard + "&card=3");
		Assert.Equal(200, page.Status);
		Assert.Contains("<h1 class=\"gp-title\">The rules</h1>", page.Body);
		Dictionary<string, string> fields = HiddenFields(page);
		fields["step"] = "card";
		fields["card"] = "5";
		Assert.Equal(Authorize, (await browser.Post("/users/agreements", fields)).Location);

		await using AppDbContext db = probe.Open();
		Assert.True(await db.UserAgreements.AnyAsync(a => a.UserID == user.ID && a.Document == "rules" && a.Version == "2026-11"));
	}

	// the Terms step records nothing unless the rules step came first: a post of the tick alone while the rules are owed
	// goes back to the cards, and a post without this browser's antiforgery token is refused
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheLastStepNeedsBothConfirmedAndTheToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await AgreementGateTests.SignedUp(probe, "Skipper", AgreementsTests.Configured());

		PageResponse card = await browser.Get(Wizard);
		Dictionary<string, string> skipped = HiddenFields(card);
		skipped["step"] = "terms";
		skipped.Remove("rules");
		skipped["agree_terms"] = "1";
		PageResponse back = await browser.Post("/users/agreements", skipped);
		Assert.Equal(302, back.Status);
		Assert.StartsWith("/users/agreements?", back.Location);

		Dictionary<string, string> forged = HiddenFields(card);
		forged.Remove("__go_consent_token");
		Assert.Equal(400, (await browser.Post("/users/agreements", forged)).Status);

		await using AppDbContext db = probe.Open();
		Assert.False(await db.UserAgreements.AnyAsync(a => a.UserID == user.ID));
	}

	// /profile lists each document with the agreed version and day, or not agreed, a link to it, and a way to agree
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheProfileShowsTheAgreements(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await AgreementGateTests.SignedUp(probe, "Profiled", AgreementsTests.Configured());
		await using (AppDbContext db = probe.Open())
		{
			db.UserAgreements.Add(new UserAgreement { UserID = user.ID, Document = "rules", Version = "2026-11", AgreedAt = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc) });
			await db.SaveChangesAsync();
		}

		PageResponse profile = await browser.Get("/profile");
		Assert.Equal(200, profile.Status);
		Assert.Contains("Agreements", profile.Body);
		Assert.Contains("Rules of Engagement", profile.Body);
		Assert.Contains("Version 2026-11, agreed on 2026-11-18", profile.Body);
		Assert.Contains("Not agreed", profile.Body);
		Assert.Contains($"href=\"{AgreementsTests.RulesUrl}\"", profile.Body);
		Assert.Contains($"href=\"{AgreementsTests.TermsUrl}\"", profile.Body);
		Assert.Contains($"href=\"{AgreementsTests.PrivacyUrl}\"", profile.Body);
		Assert.Contains("href=\"/users/agreements?ReturnUrl=%2Fprofile\"", profile.Body);

		// without agreements configured the section is not there
		AccountsBrowser plain = await AgreementGateTests.Browser(probe);
		Assert.Equal(302, (await PasswordPagesTests.SignIn(plain, "profiled@example.org", "a long enough password")).Status);
		// (the footer still links the rules, the built-in page)
		Assert.DoesNotContain("The rules and terms you agreed to on this environment.", (await plain.Get("/profile")).Body);
	}
}
