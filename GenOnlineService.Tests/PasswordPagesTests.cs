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
	private sealed class Factory : IDbContextFactory<AppDbContext>
	{
		private readonly ProbeDatabase m_probe;

		public Factory(ProbeDatabase probe)
		{
			m_probe = probe;
		}

		public AppDbContext CreateDbContext() => m_probe.Open();
	}

	public sealed record Response(int Status, string Body, string? Location, List<string> SetCookies, string FrameOptions);

	// The accounts pages in one in-process pipeline, with a cookie jar like a browser's
	private sealed class Browser
	{
		private readonly RequestDelegate m_pipeline;
		private readonly IServiceProvider m_services;
		private readonly Dictionary<string, string> m_cookies = new();

		public Browser(ProbeDatabase probe, Dictionary<string, string?> settings)
		{
			Dictionary<string, string?> values = new(settings) { ["Accounts:ephemeral_keys"] = "true", ["Core:enforce_https"] = "false" };
			AccountsSettings accounts = AccountsSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

			ServiceCollection services = new();
			services.AddLogging();
			// what a host would register for MVC
			var diagnostics = new System.Diagnostics.DiagnosticListener("PasswordPagesTests");
			services.AddSingleton(diagnostics);
			services.AddSingleton<System.Diagnostics.DiagnosticSource>(diagnostics);
			services.AddSingleton<IDbContextFactory<AppDbContext>>(new Factory(probe));
			services.AddSingleton(accounts);
			services.AddGenOnlineIdentity(accounts, development: true);
			services.AddRouting();
			services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly);
			m_services = services.BuildServiceProvider();

			var app = new ApplicationBuilder(m_services);
			app.UseRouting();
			app.UseAuthentication();
			app.UseEndpoints(endpoints => endpoints.MapControllers());
			m_pipeline = app.Build();
		}

		public IServiceProvider Services => m_services;

		public Task<Response> Get(string pathAndQuery) => Send("GET", pathAndQuery, null);

		public Task<Response> Post(string pathAndQuery, Dictionary<string, string> form) => Send("POST", pathAndQuery, form);

		private async Task<Response> Send(string method, string pathAndQuery, Dictionary<string, string>? form)
		{
			using IServiceScope scope = m_services.CreateScope();
			var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
			scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
			context.Request.Method = method;
			context.Request.Scheme = "http";
			context.Request.Host = new HostString("accounts.test");
			int query = pathAndQuery.IndexOf('?');
			context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery.Substring(0, query);
			context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(pathAndQuery.Substring(query));
			if (m_cookies.Count > 0)
			{
				context.Request.Headers.Cookie = String.Join("; ", m_cookies.Select(c => $"{c.Key}={c.Value}"));
			}
			if (form != null)
			{
				byte[] body = Encoding.UTF8.GetBytes(String.Join("&", form.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}")));
				context.Request.ContentType = "application/x-www-form-urlencoded";
				context.Request.ContentLength = body.Length;
				context.Request.Body = new MemoryStream(body);
			}
			var responseBody = new MemoryStream();
			context.Response.Body = responseBody;

			await m_pipeline(context);

			List<string> setCookies = context.Response.Headers.SetCookie.Select(c => c!).ToList();
			foreach (string cookie in setCookies)
			{
				string pair = cookie.Split(';')[0];
				int equals = pair.IndexOf('=');
				string name = pair.Substring(0, equals);
				string value = pair.Substring(equals + 1);
				if (value.Length == 0 || cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
				{
					m_cookies.Remove(name);
				}
				else
				{
					m_cookies[name] = value;
				}
			}

			return new Response(context.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()), context.Response.Headers.Location.ToString() is { Length: > 0 } l ? l : null, setCookies, context.Response.Headers.XFrameOptions.ToString());
		}

		public bool SignedIn => m_cookies.ContainsKey("go_session");

		public void SignOutLocally() => m_cookies.Remove("go_session");

		// The form's hidden antiforgery token, as a browser would post it back
		public static string Token(Response page)
		{
			System.Text.RegularExpressions.Match match = Regex.Match(page.Body, "name=\"__go_consent_token\" value=\"([^\"]+)\"");
			Assert.True(match.Success, "the page has no antiforgery field: " + page.Body);
			return WebUtility.HtmlDecode(match.Groups[1].Value);
		}
	}

	private static Dictionary<string, string?> Enabled(params (string Key, string Value)[] extra)
	{
		Dictionary<string, string?> values = new() { ["Accounts:password:enabled"] = "true" };
		foreach ((string key, string value) in extra)
		{
			values[key] = value;
		}
		return values;
	}

	private static async Task<Response> SignUp(Browser browser, string name, string email, string password, string? honeypot = null)
	{
		Response page = await browser.Get("/users/sign_up?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dx");
		Assert.Equal(200, page.Status);
		Dictionary<string, string> form = new()
		{
			["__go_consent_token"] = Browser.Token(page),
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

	private static async Task<Response> SignIn(Browser browser, string email, string password)
	{
		Response page = await browser.Get("/users/sign_in?ReturnUrl=%2Foauth%2Fauthorize%3Fclient_id%3Dx");
		Assert.Equal(200, page.Status);
		return await browser.Post("/users/sign_in", new()
		{
			["__go_consent_token"] = Browser.Token(page),
			["email"] = email,
			["password"] = password,
			["ReturnUrl"] = "/oauth/authorize?client_id=x"
		});
	}

	private static async Task<User?> UserByEmail(ProbeDatabase probe, string email)
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
		var browser = new Browser(probe, Enabled());

		Response done = await SignUp(browser, "Newcomer", "new@example.org", "a long enough password");

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

		Assert.Equal(302, (await SignUp(new Browser(probe, Enabled()), "Player", "one@example.org", "first long password")).Status);
		Assert.Equal(302, (await SignUp(new Browser(probe, Enabled()), "Player", "two@example.org", "second long password")).Status);

		Assert.Equal(401, (await SignIn(new Browser(probe, Enabled()), "two@example.org", "wrong password here")).Status);
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
		var browser = new Browser(probe, Enabled(("Accounts:password:email_domain_deny_list:0", "mailinator.com")));

		Response refused = await SignUp(browser, name, email, password, honeypot);

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
		Assert.Equal(302, (await SignUp(new Browser(probe, Enabled()), "First", "same@example.org", "a long enough password")).Status);

		Response second = await SignUp(new Browser(probe, Enabled()), "Second", "Same@Example.org", "another long password");

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
		var browser = new Browser(probe, Enabled());

		Response signUp = await browser.Post("/users/sign_up", new() { ["display_name"] = "Forged", ["email"] = "forged@example.org", ["password"] = "a long enough password" });
		Response signIn = await browser.Post("/users/sign_in", new() { ["email"] = "forged@example.org", ["password"] = "a long enough password" });

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
		Assert.Equal(302, (await SignUp(new Browser(probe, Enabled()), "Signer", "signer@example.org", "the right password")).Status);

		var browser = new Browser(probe, Enabled());
		Response right = await SignIn(browser, "SIGNER@example.org", "the right password");
		Assert.Equal(302, right.Status);
		Assert.Equal("/oauth/authorize?client_id=x", right.Location);
		Assert.True(browser.SignedIn);

		Response unknown = await SignIn(new Browser(probe, Enabled()), "nobody@example.org", "the right password");
		Assert.Equal(401, unknown.Status);
		Assert.Contains("Wrong email or password.", unknown.Body);

		for (int attempt = 1; attempt <= 5; attempt++)
		{
			Response wrong = await SignIn(new Browser(probe, Enabled()), "signer@example.org", "a wrong password");
			Assert.Equal(401, wrong.Status);
			Assert.Contains("Wrong email or password.", wrong.Body);
		}

		var locked = new Browser(probe, Enabled());
		Response lockedOut = await SignIn(locked, "signer@example.org", "the right password");
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
		Assert.Equal(302, (await SignUp(new Browser(probe, Enabled()), "Blocked", "blocked@example.org", "the right password")).Status);
		await using (AppDbContext db = probe.Open())
		{
			await db.Users.Where(u => u.Email == "blocked@example.org").ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UserStates.Blocked));
		}

		var browser = new Browser(probe, Enabled());
		Response refused = await SignIn(browser, "blocked@example.org", "the right password");

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
		Assert.Equal(302, (await SignUp(new Browser(probe, Enabled()), "Guarded", "guarded@example.org", "the right password")).Status);
		await using (AppDbContext db = probe.Open())
		{
			await db.Users.Where(u => u.Email == "guarded@example.org").ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorEnabled, true));
		}

		var browser = new Browser(probe, Enabled());
		Response response = await SignIn(browser, "guarded@example.org", "the right password");

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

		Response withPassword = await new Browser(probe, Enabled(("Accounts:providers:steam:enabled", "true"), ("Accounts:providers:steam:only_provider", "true"))).Get("/users/sign_in");
		Assert.Equal(200, withPassword.Status);
		Assert.Contains("action=\"/users/sign_in\"", withPassword.Body);
		Assert.Contains("href=\"/users/sign_up", withPassword.Body);
		Assert.Contains("Sign in with steam", withPassword.Body);
		Assert.Equal("DENY", withPassword.FrameOptions);

		var closed = new Browser(probe, Enabled(("Accounts:password:allow_sign_up", "false")));
		Response closedSignIn = await closed.Get("/users/sign_in");
		Assert.DoesNotContain("href=\"/users/sign_up", closedSignIn.Body);
		Assert.Equal(404, (await closed.Get("/users/sign_up")).Status);
		Assert.Equal(404, (await closed.Post("/users/sign_up", new())).Status);

		var off = new Browser(probe, new());
		Assert.Equal(503, (await off.Get("/users/sign_in")).Status);
		Assert.Equal(404, (await off.Get("/users/sign_up")).Status);
		Assert.Equal(404, (await off.Post("/users/sign_in", new())).Status);
	}
}
