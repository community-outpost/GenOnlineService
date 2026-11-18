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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// Mail that is never sent: what the pages handed to the sender
internal sealed class RecordingEmailSender : IEmailSender
{
	public bool Enabled { get; init; } = true;

	public List<EmailMessage> Sent { get; } = new();

	public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
	{
		lock (Sent)
		{
			Sent.Add(message);
		}
		return Task.CompletedTask;
	}

	// The one link in a mail's plain-text part, as a path and query on the accounts host
	public static string LinkIn(EmailMessage message, string host = "http://accounts.test")
	{
		System.Text.RegularExpressions.Match match = Regex.Match(message.Text, @"https?://\S+");
		Assert.True(match.Success, "the mail has no link: " + message.Text);
		Assert.StartsWith(host + "/", match.Value);
		Assert.Contains(System.Net.WebUtility.HtmlEncode(match.Value), message.Html);
		return match.Value.Substring(host.Length);
	}
}

// Email confirmation (identity hub T4c, S4): with mail, a password sign-up gets a confirmation link and /oauth/authorize
// waits for it; without mail nothing is sent and nothing waits
public class EmailConfirmationTests
{
	internal const string AuthorizeQuery = "?client_id=genonline_zerohour&response_type=code&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcallback&scope=openid%20game&state=s&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";

	internal static Dictionary<string, string?> WithGameClient(params (string Key, string Value)[] extra) => PasswordPagesTests.Enabled(new[]
	{
		// the public origin of mail links, required with mail (AddMail refuses to start without it)
		("Accounts:issuer", "http://accounts.test/"),
		("Accounts:clients:0:client_id", "genonline_zerohour"),
		("Accounts:clients:0:display_name", "Generals Online (Zero Hour)"),
		("Accounts:clients:0:kind", "game"),
		("Accounts:clients:0:game", "zerohour"),
		("Accounts:clients:0:redirect_uris:0", "http://127.0.0.1/callback")
	}.Concat(extra).ToArray());

	internal static async Task<AccountsBrowser> Browser(ProbeDatabase probe, RecordingEmailSender? mail, Action<IServiceCollection>? configure = null, params (string Key, string Value)[] extra)
	{
		var browser = new AccountsBrowser(probe, WithGameClient(extra), services =>
		{
			if (mail != null)
			{
				services.AddSingleton<IEmailSender>(mail);
			}
			configure?.Invoke(services);
		});
		await AccountsSetup.SeedClients(browser.Services, browser.Settings);
		await using AppDbContext db = probe.Open();
		await ClientRegistry.Reload(db);
		return browser;
	}

	private static async Task<PageResponse> Resend(AccountsBrowser browser)
	{
		PageResponse page = await browser.Get("/users/confirmation/new?ReturnUrl=%2Fprofile");
		Assert.Equal(200, page.Status);
		return await browser.Post("/users/confirmation", new() { ["__go_consent_token"] = AccountsBrowser.Token(page), ["ReturnUrl"] = "/profile" });
	}

	// without mail nothing is sent, the address stays unconfirmed and authorize issues a code as before
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task WithoutMailNothingIsSentAndNobodyWaits(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var mail = new RecordingEmailSender { Enabled = false };
		AccountsBrowser browser = await Browser(probe, mail);

		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "NoMail", "nomail@example.org", "a long enough password")).Status);

		Assert.Empty(mail.Sent);
		Assert.Null((await PasswordPagesTests.UserByEmail(probe, "nomail@example.org"))!.EmailConfirmedAt);
		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get("/oauth/authorize" + AuthorizeQuery)).Location);
		Assert.Equal(404, (await browser.Get("/users/confirmation/new")).Status);
		Assert.Equal(404, (await browser.Get("/users/confirmation?confirmation_token=x")).Status);
	}

	// with mail, sign-up still signs in and goes on, and sends exactly one mail with the confirmation link
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ASignUpSendsOneConfirmationMail(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var mail = new RecordingEmailSender();
		AccountsBrowser browser = await Browser(probe, mail);

		PageResponse done = await PasswordPagesTests.SignUp(browser, "Confirmer", "confirmer@example.org", "a long enough password");

		Assert.Equal(302, done.Status);
		Assert.Equal("/oauth/authorize?client_id=x", done.Location);
		Assert.True(browser.SignedIn);
		EmailMessage sent = Assert.Single(mail.Sent);
		User user = (await PasswordPagesTests.UserByEmail(probe, "confirmer@example.org"))!;
		Assert.Equal("confirmer@example.org", sent.To);
		Assert.Equal("Confirm your email address", sent.Subject);
		Assert.Contains($"Hello Confirmer#{user.NameTag:D4},", sent.Text);
		Assert.StartsWith("/users/confirmation?confirmation_token=", RecordingEmailSender.LinkIn(sent));
		Assert.Null(user.EmailConfirmedAt);
	}

	// an unconfirmed password account gets no token: authorize sends it to "check your email", prompt=none asks to interact
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnUnconfirmedAccountWaitsAtAuthorize(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		AccountsBrowser browser = await Browser(probe, new RecordingEmailSender());
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Waiter", "waiter@example.org", "a long enough password")).Status);

		PageResponse gated = await browser.Get("/oauth/authorize" + AuthorizeQuery);
		Assert.Equal(302, gated.Status);
		Assert.StartsWith("/users/confirmation/new?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dgenonline_zerohour", gated.Location);

		PageResponse silent = await browser.Get("/oauth/authorize" + AuthorizeQuery + "&prompt=none");
		Assert.Contains("error=interaction_required", silent.Location);

		PageResponse waiting = await browser.Get(gated.Location!);
		Assert.Equal(200, waiting.Status);
		Assert.Contains("Check your email", waiting.Body);
		Assert.Contains("waiter@example.org", waiting.Body);
		Assert.Contains("Send the link again", waiting.Body);
	}

	// the link confirms the address and authorize goes on; a forged, foreign or expired link is refused
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheLinkConfirmsTheAddress(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var mail = new RecordingEmailSender();
		AccountsBrowser browser = await Browser(probe, mail);
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Linker", "linker@example.org", "a long enough password")).Status);
		User user = (await PasswordPagesTests.UserByEmail(probe, "linker@example.org"))!;
		string link = RecordingEmailSender.LinkIn(Assert.Single(mail.Sent));
		string token = Uri.UnescapeDataString(link.Substring(link.IndexOf('=') + 1));
		Assert.True(AccountMail.TryUnpack(token, out long id, out string raw));

		PageResponse forged = await browser.Get("/users/confirmation?confirmation_token=" + Uri.EscapeDataString(AccountMail.Pack(id, raw + "x")));
		Assert.Equal(400, forged.Status);
		Assert.Contains("not valid or has expired", forged.Body);
		Assert.Equal(400, (await browser.Get("/users/confirmation?confirmation_token=garbage")).Status);
		Assert.Equal(400, (await browser.Get("/users/confirmation?confirmation_token=" + Uri.EscapeDataString(AccountMail.Pack(id + 1, raw)))).Status);
		Assert.Null((await PasswordPagesTests.UserByEmail(probe, "linker@example.org"))!.EmailConfirmedAt);

		PageResponse confirmed = await browser.Get(link);
		Assert.Equal(200, confirmed.Status);
		Assert.Contains("Email address confirmed", confirmed.Body);
		Assert.NotNull((await PasswordPagesTests.UserByEmail(probe, "linker@example.org"))!.EmailConfirmedAt);
		Assert.Equal(user.ID, id);

		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get("/oauth/authorize" + AuthorizeQuery)).Location);
		// nothing to wait for any more
		Assert.Equal("/profile", (await browser.Get("/users/confirmation/new?ReturnUrl=%2Fprofile")).Location);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task AnExpiredLinkIsRefused(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var mail = new RecordingEmailSender();
		AccountsBrowser browser = await Browser(probe, mail,
			services => services.PostConfigure<EmailConfirmationTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromSeconds(-1)));
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Late", "late@example.org", "a long enough password")).Status);

		PageResponse expired = await browser.Get(RecordingEmailSender.LinkIn(Assert.Single(mail.Sent)));

		Assert.Equal(400, expired.Status);
		Assert.Null((await PasswordPagesTests.UserByEmail(probe, "late@example.org"))!.EmailConfirmedAt);
	}

	// resending mails a new link, three times per account in the window; the client address has its own, larger limit
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ResendingIsRateLimited(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var mail = new RecordingEmailSender();
		AccountsBrowser browser = await Browser(probe, mail);
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Resender", "resender@example.org", "a long enough password")).Status);

		for (int i = 0; i < AccountMailLimiter.PerAccount; i++)
		{
			PageResponse resent = await Resend(browser);
			Assert.Equal(200, resent.Status);
			Assert.Contains("A new link is on its way.", resent.Body);
		}
		PageResponse refused = await Resend(browser);
		Assert.Equal(429, refused.Status);
		Assert.Contains("Too many mails were asked for.", refused.Body);
		Assert.Equal(1 + AccountMailLimiter.PerAccount, mail.Sent.Count);

		// other accounts from the same address: 10 resends in all, then the address is refused too
		int fromAddress = AccountMailLimiter.PerAccount;
		for (int account = 0; fromAddress < AccountMailLimiter.PerClientAddress; account++)
		{
			// another browser on the same host (one limiter), with its own cookies
			AccountsBrowser other = browser.Fork();
			Assert.Equal(302, (await PasswordPagesTests.SignUp(other, "Other", $"other{account}@example.org", "a long enough password")).Status);
			for (int i = 0; i < AccountMailLimiter.PerAccount && fromAddress < AccountMailLimiter.PerClientAddress; i++, fromAddress++)
			{
				Assert.Equal(200, (await Resend(other)).Status);
			}
			if (fromAddress == AccountMailLimiter.PerClientAddress)
			{
				Assert.Equal(429, (await Resend(other)).Status);
			}
		}
	}

	// accounts that existed before mail confirmation count as confirmed (the migration's data step) and pass authorize
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task GrandfatheredAccountsPass(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		// signed up while mail was off
		Assert.Equal(302, (await PasswordPagesTests.SignUp(await Browser(probe, null), "Veteran", "veteran@example.org", "a long enough password")).Status);
		Assert.Equal(302, (await PasswordPagesTests.SignUp(await Browser(probe, null), "Linked", "linked@example.org", "a long enough password")).Status);
		await using (AppDbContext db = probe.Open())
		{
			// a provider-only account has no password; a confirmed one keeps its own time
			User linked = await db.Users.AsTracking().SingleAsync(u => u.Email == "linked@example.org");
			linked.PasswordHash = null;
			await db.SaveChangesAsync();
		}

		await using (AppDbContext db = probe.Open())
		{
			await db.Database.ExecuteSqlRawAsync(provider == "postgres"
				? GenOnlineService.Migrations.Postgres.PasswordAccountsConfirmed.UpSql
				: GenOnlineService.Migrations.MySql.PasswordAccountsConfirmed.UpSql);
		}
		User veteran = (await PasswordPagesTests.UserByEmail(probe, "veteran@example.org"))!;
		Assert.Equal(veteran.CreatedAt, veteran.EmailConfirmedAt);
		Assert.Null((await PasswordPagesTests.UserByEmail(probe, "linked@example.org"))!.EmailConfirmedAt);

		var mail = new RecordingEmailSender();
		AccountsBrowser browser = await Browser(probe, mail);
		Assert.Equal(302, (await PasswordPagesTests.SignIn(browser, "veteran@example.org", "a long enough password")).Status);
		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get("/oauth/authorize" + AuthorizeQuery)).Location);
		Assert.Empty(mail.Sent);

		// Down clears exactly the grandfathered rows
		await using (AppDbContext db = probe.Open())
		{
			await db.Database.ExecuteSqlRawAsync(provider == "postgres"
				? GenOnlineService.Migrations.Postgres.PasswordAccountsConfirmed.DownSql
				: GenOnlineService.Migrations.MySql.PasswordAccountsConfirmed.DownSql);
		}
		Assert.Null((await PasswordPagesTests.UserByEmail(probe, "veteran@example.org"))!.EmailConfirmedAt);
	}

	// an account without a password (a provider sign-in) never waits, even with an unconfirmed address
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProviderAccountsAreUnaffected(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		AccountsBrowser browser = await Browser(probe, new RecordingEmailSender());
		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Provided", "provided@example.org", "a long enough password")).Status);
		await using (AppDbContext db = probe.Open())
		{
			User user = await db.Users.AsTracking().SingleAsync(u => u.Email == "provided@example.org");
			user.PasswordHash = null;
			await db.SaveChangesAsync();
		}

		Assert.StartsWith("http://127.0.0.1/callback?code=", (await browser.Get("/oauth/authorize" + AuthorizeQuery)).Location);
	}

	// with mail, an unconfirmed account still signs in (only tokens wait), and two-factor sign-in still asks for its code
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignInAndTwoFactorStillWorkWithMail(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		Assert.Equal(302, (await PasswordPagesTests.SignUp(await Browser(probe, new RecordingEmailSender()), "Signer", "signer@example.org", "a long enough password")).Status);

		AccountsBrowser browser = await Browser(probe, new RecordingEmailSender());
		PageResponse signedIn = await PasswordPagesTests.SignIn(browser, "signer@example.org", "a long enough password");
		Assert.Equal(302, signedIn.Status);
		Assert.Equal("/oauth/authorize?client_id=x", signedIn.Location);
		Assert.True(browser.SignedIn);

		await using (AppDbContext db = probe.Open())
		{
			User user = await db.Users.AsTracking().SingleAsync(u => u.Email == "signer@example.org");
			user.TwoFactorEnabled = true;
			await db.SaveChangesAsync();
		}
		AccountsBrowser second = await Browser(probe, new RecordingEmailSender());
		PageResponse code = await PasswordPagesTests.SignIn(second, "signer@example.org", "a long enough password");
		Assert.Equal(302, code.Status);
		Assert.StartsWith("/users/two_factor?ReturnUrl=", code.Location);
		Assert.False(second.SignedIn);
	}
}
