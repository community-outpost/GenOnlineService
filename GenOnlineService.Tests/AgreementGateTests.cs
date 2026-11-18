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
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The sign-in gate of agreement tracking (identity hub T18b, S19): /oauth/authorize issues no code while an agreement is
// owed, after the confirmation, two-factor setup and name gates; the game client's refresh is refused while owed and the
// player has no live game connection
[Collection(AdminAreaCollection.Name)]
public class AgreementGateTests
{
	internal const string AuthorizeQuery = EmailConfirmationTests.AuthorizeQuery;
	internal const string GatedPrefix = "/users/agreements?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dgenonline_zerohour";

	// a game client browser, with the agreements configured unless none is given
	internal static Task<AccountsBrowser> Browser(ProbeDatabase probe, params (string Key, string Value)[] agreements)
		=> EmailConfirmationTests.Browser(probe, null, null, agreements);

	internal static async Task<(AccountsBrowser Browser, User User)> SignedUp(ProbeDatabase probe, string name, params (string Key, string Value)[] agreements)
	{
		AccountsBrowser browser = await Browser(probe, agreements);
		string email = name.ToLowerInvariant() + "@example.org";
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, name, email, "a long enough password")).Status);
		return (browser, (await PasswordPagesTests.UserByEmail(probe, email))!);
	}

	internal static async Task Agree(ProbeDatabase probe, long userID, string document, string version)
	{
		await using AppDbContext db = probe.Open();
		db.UserAgreements.Add(new UserAgreement { UserID = userID, Document = document, Version = version, AgreedAt = DateTime.UtcNow });
		await db.SaveChangesAsync();
	}

	// without any version configured the sign-in is exactly as before: a code at once
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task NothingConfiguredGatesNothing(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, _) = await SignedUp(probe, "Free");

		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get("/oauth/authorize" + AuthorizeQuery)).Location);
		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get("/oauth/authorize" + AuthorizeQuery + "&prompt=none")).Location);
	}

	// an account that owes an agreement is sent to the agreements and back; a silent request is told to ask
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnOwingPlayerIsSentToTheAgreements(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await SignedUp(probe, "Owing", AgreementsTests.Configured());

		PageResponse gated = await browser.Get("/oauth/authorize" + AuthorizeQuery);
		Assert.Equal(302, gated.Status);
		Assert.StartsWith(GatedPrefix, gated.Location);

		PageResponse silent = await browser.Get("/oauth/authorize" + AuthorizeQuery + "&prompt=none");
		Assert.Contains("error=interaction_required", silent.Location);

		// one of two is still owed
		await Agree(probe, user.ID, "rules", "2026-11");
		Assert.StartsWith(GatedPrefix, (await browser.Get("/oauth/authorize" + AuthorizeQuery)).Location);

		await Agree(probe, user.ID, "terms", "1");
		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get("/oauth/authorize" + AuthorizeQuery)).Location);
	}

	// an agreement to an older version does not count
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnOlderVersionIsOwedAgain(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await SignedUp(probe, "Older", AgreementsTests.Configured(rules: "2026-12"));
		await Agree(probe, user.ID, "rules", "2026-11");
		await Agree(probe, user.ID, "terms", "1");

		Assert.StartsWith(GatedPrefix, (await browser.Get("/oauth/authorize" + AuthorizeQuery)).Location);
	}

	// an account without a password, as a provider sign-up leaves it, is gated the same way
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AProviderAccountIsGatedToo(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await SignedUp(probe, "Linked", AgreementsTests.Configured());
		await using (AppDbContext db = probe.Open())
		{
			await db.Users.Where(u => u.ID == user.ID).ExecuteUpdateAsync(set => set.SetProperty(u => u.PasswordHash, (string?)null));
			db.Identities.Add(new UserIdentity { UserID = user.ID, Provider = "discord", ExternUid = "80351110224678999", CreatedAt = DateTime.UtcNow });
			await db.SaveChangesAsync();
		}

		Assert.StartsWith(GatedPrefix, (await browser.Get("/oauth/authorize" + AuthorizeQuery)).Location);
		Assert.Contains("error=interaction_required", (await browser.Get("/oauth/authorize" + AuthorizeQuery + "&prompt=none")).Location);
	}

	// the name gate runs before the agreements and behaves as before
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheNameComesBeforeTheAgreements(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await SignedUp(probe, "Unnamed", AgreementsTests.Configured());
		await using (AppDbContext db = probe.Open())
		{
			await db.Users.Where(u => u.ID == user.ID).ExecuteUpdateAsync(set => set.SetProperty(u => u.NameRequiredAt, DateTime.UtcNow));
		}

		PageResponse gated = await browser.Get("/oauth/authorize" + AuthorizeQuery);
		Assert.StartsWith("/users/sign_up/welcome?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dgenonline_zerohour", gated.Location);
		Assert.Contains("error=interaction_required", (await browser.Get("/oauth/authorize" + AuthorizeQuery + "&prompt=none")).Location);
	}

	// the email confirmation gate runs before the agreements and behaves as before
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ConfirmationComesBeforeTheAgreements(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		AccountsBrowser browser = await EmailConfirmationTests.Browser(probe, new RecordingEmailSender(), null, AgreementsTests.Configured());
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Waiter", "waiter@example.org", "a long enough password")).Status);

		PageResponse gated = await browser.Get("/oauth/authorize" + AuthorizeQuery);
		Assert.StartsWith("/users/confirmation/new?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dgenonline_zerohour", gated.Location);
		Assert.Contains("error=interaction_required", (await browser.Get("/oauth/authorize" + AuthorizeQuery + "&prompt=none")).Location);
	}

	// the two-factor setup gate of password staff runs before the agreements and behaves as before
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TwoFactorSetupComesBeforeTheAgreements(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await SignedUp(probe, "Mod", AgreementsTests.Configured());
		await using (AppDbContext db = probe.Open())
		{
			db.UserRoles.Add(new UserRole { UserID = user.ID, Role = StaffRoles.Moderator, Source = AuthoritySources.Local, GrantedAt = DateTime.UtcNow, GrantedBy = "test" });
			await db.SaveChangesAsync();
		}

		PageResponse gated = await browser.Get("/oauth/authorize" + AuthorizeQuery);
		Assert.StartsWith("/profile/two_factor_auth?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dgenonline_zerohour", gated.Location);
		Assert.Contains("error=interaction_required", (await browser.Get("/oauth/authorize" + AuthorizeQuery + "&prompt=none")).Location);
	}

	// The player's realtime connection on this copy of the service, as the hub keeps it; the test adds one and removes it
	internal static IDisposable Connected(long userID, string name)
	{
		var shared = (ConcurrentDictionary<long, SharedUserData>)typeof(WebSocketManager).GetField("m_dictSharedUserData", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
		var social = new UserSocialContainer { Friends = new HashSet<long>(), PendingRequests = new HashSet<long>(), Blocked = new HashSet<long>() };
		shared[userID] = new SharedUserData(userID, social, name, 1, new PlayerStats(userID, 1500, 0, 1500));
		var sessions = WebSocketManager.GetUserDataCache().GetOrAdd(EUserSessionType.GameClient, _ => new ConcurrentDictionary<long, UserSession>());
		sessions[userID] = new UserSession(userID, EUserSessionType.GameClient, "genonline_zerohour", "", "", 0, 0);
		return new Undo(() =>
		{
			sessions.TryRemove(userID, out _);
			shared.TryRemove(userID, out _);
		});
	}

	private sealed class Undo(Action action) : IDisposable
	{
		public void Dispose() => action();
	}

	private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	// A code and then a refresh token for the game client, once nothing is owed
	internal static async Task<string> RefreshToken(AccountsBrowser browser)
	{
		string verifier = "a-code-verifier-long-enough-for-pkce-0123456789-abcdefghij";
		string challenge = Base64UrlEncode(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
		string query = $"?client_id=genonline_zerohour&response_type=code&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcallback&scope=openid%20offline_access%20game&state=s&code_challenge={challenge}&code_challenge_method=S256";
		PageResponse authorized = await browser.Get("/oauth/authorize" + query);
		Assert.StartsWith("http://127.0.0.1/callback?code=", authorized.Location);
		string code = System.Web.HttpUtility.ParseQueryString(new Uri(authorized.Location!).Query)["code"]!;

		PageResponse issued = await browser.Post("/oauth/token", new()
		{
			["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = "http://127.0.0.1/callback",
			["client_id"] = "genonline_zerohour", ["code_verifier"] = verifier
		});
		Assert.Equal(200, issued.Status);
		return JsonDocument.Parse(issued.Body).RootElement.GetProperty("refresh_token").GetString()!;
	}

	internal static Task<PageResponse> Refresh(AccountsBrowser browser, string refreshToken)
		=> browser.Post("/oauth/token", new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = "genonline_zerohour" });

	// at login (no live game connection) an owed agreement refuses the game client's refresh, so it signs in again in the
	// browser and meets the agreement pages; mid-session (connected) the refresh passes and the pages wait for the next login
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ARefreshIsRefusedWhileOwedAndNotConnected(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AccountsBrowser browser, User user) = await SignedUp(probe, "Refresher", AgreementsTests.Configured());
		await Agree(probe, user.ID, "rules", "2026-11");
		await Agree(probe, user.ID, "terms", "1");
		string refresh = await RefreshToken(browser);

		// nothing owed: the refresh works
		PageResponse refreshed = await Refresh(browser, refresh);
		Assert.Equal(200, refreshed.Status);
		refresh = JsonDocument.Parse(refreshed.Body).RootElement.GetProperty("refresh_token").GetString()!;

		// a new version is owed now
		await using (AppDbContext db = probe.Open())
		{
			await db.UserAgreements.Where(a => a.UserID == user.ID && a.Document == "rules").ExecuteDeleteAsync();
		}

		// connected: the refresh passes
		using (Connected(user.ID, "Refresher"))
		{
			PageResponse connected = await Refresh(browser, refresh);
			Assert.Equal(200, connected.Status);
			refresh = JsonDocument.Parse(connected.Body).RootElement.GetProperty("refresh_token").GetString()!;
		}

		// not connected: refused, naming the agreements
		PageResponse refused = await Refresh(browser, refresh);
		Assert.Equal(400, refused.Status);
		JsonElement error = JsonDocument.Parse(refused.Body).RootElement;
		Assert.Equal("invalid_grant", error.GetProperty("error").GetString());
		Assert.Contains("agreements", error.GetProperty("error_description").GetString());
	}
}
