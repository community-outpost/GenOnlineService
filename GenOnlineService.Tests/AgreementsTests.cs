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
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// Agreement tracking (identity hub T18, S19): each account agrees to the current Rules of Engagement and Terms of Service
// before playing; the versions, days and links are configuration, and nothing is gated without them
[Collection(AdminAreaCollection.Name)]
public class AgreementsTests
{
	internal const string RulesUrl = "https://example.org/rules";
	internal const string TermsUrl = "https://example.org/terms";
	internal const string PrivacyUrl = "https://example.org/privacy";

	internal static (string Key, string Value)[] Configured(string rules = "2026-11", string terms = "1") =>
	[
		("Agreements:rules:version", rules),
		("Agreements:rules:date", "2026-11-18"),
		("Agreements:rules:url", RulesUrl),
		("Agreements:terms:version", terms),
		("Agreements:terms:date", "2026-11-01"),
		("Agreements:terms:url", TermsUrl),
		("Agreements:privacy:url", PrivacyUrl)
	];

	private static AgreementsSettings Read(params (string Key, string Value)[] values)
		=> AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build()).Agreements;

	// with no version configured nothing is required, so nothing can be owed
	[Fact]
	public void NothingConfiguredRequiresNothing()
	{
		AgreementsSettings settings = Read();
		Assert.False(settings.Any);
		Assert.Empty(settings.Required);
	}

	[Theory]
	[InlineData("18.11.2026")]
	[InlineData("2026-13-01")]
	[InlineData("tomorrow")]
	public void AMalformedDateStopsTheStart(string date)
	{
		var error = Assert.Throws<InvalidOperationException>(() => Read(("Agreements:terms:version", "1"), ("Agreements:terms:url", TermsUrl), ("Agreements:terms:date", date)));
		Assert.Equal("Agreements:terms:date must be a day as yyyy-MM-dd, such as 2026-11-18.", error.Message);
	}

	// only the Terms configured: the rules are not required
	[Fact]
	public void ADocumentWithoutAVersionIsNotRequired()
	{
		AgreementsSettings settings = Read(("Agreements:terms:version", "1"), ("Agreements:terms:url", TermsUrl));
		Assert.Equal(new[] { AgreementsSettings.Terms }, settings.Required.Select(d => d.Name));
	}

	private static async Task<(AdminBrowser Browser, User User)> PlayerAsync(ProbeDatabase probe, string name, params (string Key, string Value)[] settings)
	{
		var browser = new AdminBrowser(probe, settings);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		User user = (await PasswordPagesTests.UserByEmail(probe, email))!;
		await AgreementGateTests.Owing(probe, user.ID);
		return (browser, user);
	}

	// a moderator, whose staff host page hands out the antiforgery header token a cookie change needs
	private static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, params (string Key, string Value)[] settings)
	{
		(AdminBrowser browser, User user) = await PlayerAsync(probe, name, settings);
		await AdminArea.Grant(probe, user.Email!, StaffRoles.Moderator);
		return (browser, user);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task NothingConfiguredReportsNothingOwed(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser browser, _) = await PlayerAsync(probe, "Plain");

		PageResponse state = await browser.Get("/api/v1/me/agreements");
		Assert.Equal(200, state.Status);
		JsonElement root = JsonDocument.Parse(state.Body).RootElement;
		Assert.Equal(0, root.GetProperty("documents").GetArrayLength());
		Assert.Equal(0, root.GetProperty("owed").GetArrayLength());
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheStateNamesTheCurrentVersionsAndWhatIsOwed(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser browser, _) = await PlayerAsync(probe, "Fresh", Configured());

		JsonElement root = JsonDocument.Parse((await browser.Get("/api/v1/me/agreements")).Body).RootElement;
		Assert.Equal(new[] { "rules", "terms" }, root.GetProperty("owed").EnumerateArray().Select(e => e.GetString()));
		Assert.Equal(PrivacyUrl, root.GetProperty("privacy_url").GetString());
		JsonElement rules = root.GetProperty("documents")[0];
		Assert.Equal("rules", rules.GetProperty("document").GetString());
		Assert.Equal("2026-11", rules.GetProperty("version").GetString());
		Assert.Equal("2026-11-18", rules.GetProperty("date").GetString());
		Assert.Equal(RulesUrl, rules.GetProperty("url").GetString());
		Assert.Equal(JsonValueKind.Null, rules.GetProperty("agreed").ValueKind);
		Assert.True(rules.GetProperty("owed").GetBoolean());
	}

	// only the version in force is accepted: an old or unknown version, or an unconfigured document, is 400 and stores nothing
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AStaleVersionIsRefused(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser browser, User user) = await StaffAsync(probe, "Stale", Configured());
		Dictionary<string, string> csrf = await browser.Csrf();

		PageResponse stale = await browser.SendJson("POST", "/api/v1/me/agreements", new { document = "rules", version = "2026-10" }, csrf);
		Assert.Equal(400, stale.Status);
		Assert.Equal("invalid_argument", AdminArea.ErrorOf(stale));
		Assert.Equal("version", JsonDocument.Parse(stale.Body).RootElement.GetProperty("params").GetProperty("field").GetString());

		PageResponse unknown = await browser.SendJson("POST", "/api/v1/me/agreements", new { document = "privacy", version = "1" }, csrf);
		Assert.Equal(400, unknown.Status);
		Assert.Equal("document", JsonDocument.Parse(unknown.Body).RootElement.GetProperty("params").GetProperty("field").GetString());

		await using AppDbContext db = probe.Open();
		Assert.False(await db.UserAgreements.AnyAsync(a => a.UserID == user.ID));
	}

	// agreeing stores the version with its time and one agreement_accepted audit row; agreeing again changes nothing
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AgreeingIsRecordedOnceAndAudited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser browser, User user) = await StaffAsync(probe, "Signer", Configured());
		Dictionary<string, string> csrf = await browser.Csrf();

		Assert.Equal(204, (await browser.SendJson("POST", "/api/v1/me/agreements", new { document = "rules", version = "2026-11" }, csrf)).Status);
		Assert.Equal(204, (await browser.SendJson("POST", "/api/v1/me/agreements", new { document = "rules", version = "2026-11" }, csrf)).Status);

		JsonElement root = JsonDocument.Parse((await browser.Get("/api/v1/me/agreements")).Body).RootElement;
		Assert.Equal(new[] { "terms" }, root.GetProperty("owed").EnumerateArray().Select(e => e.GetString()));
		JsonElement agreed = root.GetProperty("documents")[0].GetProperty("agreed");
		Assert.Equal("2026-11", agreed.GetProperty("version").GetString());
		Assert.True(agreed.GetProperty("agreed_at").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));

		await using AppDbContext db = probe.Open();
		UserAgreement row = await db.UserAgreements.SingleAsync(a => a.UserID == user.ID);
		Assert.Equal(("rules", "2026-11"), (row.Document, row.Version));
		AuditLogEntry audit = await db.AuditLog.SingleAsync(a => a.Action == Agreements.AcceptedAction);
		Assert.Equal((AuditActorKinds.User, (long?)user.ID, (long?)user.ID, AuditTargetKinds.User), (audit.ActorKind, audit.ActorID, audit.TargetID, audit.TargetKind));
		Assert.Equal("api", audit.Source);
		JsonElement details = JsonDocument.Parse(audit.Details!).RootElement;
		Assert.Equal("rules", details.GetProperty("document").GetString());
		Assert.Equal("2026-11", details.GetProperty("version").GetString());
	}

	// a cookie change still needs this user's antiforgery header, as every cookie route
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ACookieAgreementNeedsTheHeaderToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser browser, _) = await StaffAsync(probe, "Forged", Configured());

		PageResponse missing = await browser.SendJson("POST", "/api/v1/me/agreements", new { document = "rules", version = "2026-11" });
		Assert.Equal(400, missing.Status);
		Assert.Equal(Api.ApiAuthentication.CsrfError, AdminArea.ErrorOf(missing));
	}

	// the staff player page shows each document in force with the version the player agreed and when, or not agreed
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheStaffViewShowsTheAgreedVersions(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", Configured(rules: "2026-12"));
		(_, User player) = await PlayerAsync(probe, "Viewed", Configured(rules: "2026-12"));
		DateTime agreedAt = new(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc);
		await using (AppDbContext db = probe.Open())
		{
			db.UserAgreements.Add(new UserAgreement { UserID = player.ID, Document = "rules", Version = "2026-11", AgreedAt = agreedAt });
			await db.SaveChangesAsync();
		}

		PageResponse view = await moderator.Get($"/api/v1/players/{player.ID}/staff_view");
		Assert.Equal(200, view.Status);
		JsonElement agreements = JsonDocument.Parse(view.Body).RootElement.GetProperty("agreements");
		Assert.Equal(2, agreements.GetArrayLength());
		JsonElement rules = agreements[0];
		Assert.Equal("rules", rules.GetProperty("document").GetString());
		Assert.Equal("2026-12", rules.GetProperty("version").GetString());
		Assert.Equal("2026-11", rules.GetProperty("agreed_version").GetString());
		Assert.Equal(agreedAt, rules.GetProperty("agreed_at").GetDateTime().ToUniversalTime());
		Assert.False(rules.GetProperty("current").GetBoolean());
		JsonElement terms = agreements[1];
		Assert.Equal("terms", terms.GetProperty("document").GetString());
		Assert.Equal(JsonValueKind.Null, terms.GetProperty("agreed_version").ValueKind);
	}
}
