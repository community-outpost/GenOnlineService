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

using GenOnlineService.Api;
using GenOnlineService.Identity;
using GenOnlineService.Portal;
using GenOnlineService.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// The rank probe reloads the process-wide StaffRoles cache and the bearer probe the process-wide API token cache,
// so the classes of this collection run alone, after the parallel tests
[CollectionDefinition(AdminAreaCollection.Name, DisableParallelization = true)]
public sealed class AdminAreaCollection
{
	public const string Name = "Admin area (process-wide roles)";
}

// The accounts pages, the staff pages and /api/v1 in one in-process pipeline, as Program wires them
internal sealed class AdminBrowser
{
	public const string Host = "accounts.test";

	private readonly RequestDelegate m_pipeline;
	private readonly IServiceProvider m_services;
	private readonly Dictionary<string, string> m_cookies = new();

	public AdminBrowser(ProbeDatabase probe)
	{
		Dictionary<string, string?> values = new(PasswordPagesTests.Enabled()) { ["Accounts:ephemeral_keys"] = "true", ["Core:enforce_https"] = "false" };
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
		AccountsSettings accounts = AccountsSettings.Read(configuration);

		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton(configuration);
		var diagnostics = new System.Diagnostics.DiagnosticListener(nameof(AdminAreaTests));
		services.AddSingleton(diagnostics);
		services.AddSingleton<System.Diagnostics.DiagnosticSource>(diagnostics);
		services.AddSingleton<IWebHostEnvironment>(new TestEnvironment());
		services.AddSingleton<IHostEnvironment>(sp => sp.GetRequiredService<IWebHostEnvironment>());
		services.AddSingleton<IDbContextFactory<AppDbContext>>(new ProbeFactory(probe));
		services.AddSingleton(accounts);
		services.AddApiAuthentication(signInCookie: true);
		services.AddGenOnlineIdentity(accounts, development: true);
		services.AddRouting();
		services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly);
		// the perm:<name> policies of the host; the handler maps the probe database's roles with the real role table,
		// as the process-wide role cache (StaffRoles) would mix up the user ids of parallel probe databases
		services.AddAuthorization();
		services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
		services.AddSingleton<IAuthorizationHandler, ProbeRolesHandler>();
		services.AddSingleton<IAuthorizationHandler, TokenPermissionHandler>();
		PortalSetup.AddPortal(services);
		m_services = services.BuildServiceProvider();

		var app = new ApplicationBuilder(m_services);
		app.UseRouting();
		app.Use(RestSetup.StatusBodies);
		app.UseAuthentication();
		app.UseSignInCookieRules();
		app.UseAuthorization();
		app.UsePortal();
		app.UseEndpoints(endpoints =>
		{
			endpoints.MapControllers();
			endpoints.MapPortal();
		});
		m_pipeline = app.Build();
	}

	public Task<PageResponse> Get(string path, Dictionary<string, string>? headers = null) => Send("GET", path, null, null, headers);

	public Task<PageResponse> Post(string path, Dictionary<string, string> form) => Send("POST", path, form, null, null);

	public Task<PageResponse> Patch(string path, object json, Dictionary<string, string>? headers = null) => Send("PATCH", path, null, json, headers);

	// any method with a JSON body (POST, DELETE)
	public Task<PageResponse> SendJson(string method, string path, object json, Dictionary<string, string>? headers = null) => Send(method, path, null, json, headers);

	private async Task<PageResponse> Send(string method, string pathAndQuery, Dictionary<string, string>? form, object? json, Dictionary<string, string>? headers)
	{
		using IServiceScope scope = m_services.CreateScope();
		var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
		scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
		context.Request.Method = method;
		context.Request.Scheme = "http";
		context.Request.Host = new HostString(Host);
		int query = pathAndQuery.IndexOf('?');
		context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery.Substring(0, query);
		context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(pathAndQuery.Substring(query));
		if (m_cookies.Count > 0)
		{
			context.Request.Headers.Cookie = String.Join("; ", m_cookies.Select(c => $"{c.Key}={c.Value}"));
		}
		foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
		{
			context.Request.Headers[name] = value;
		}
		byte[]? body = null;
		if (form != null)
		{
			body = Encoding.UTF8.GetBytes(String.Join("&", form.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}")));
			context.Request.ContentType = "application/x-www-form-urlencoded";
		}
		else if (json != null)
		{
			body = JsonSerializer.SerializeToUtf8Bytes(json);
			context.Request.ContentType = "application/json";
		}
		if (body != null)
		{
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

		return new PageResponse(context.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()), context.Response.Headers.Location.ToString() is { Length: > 0 } l ? l : null, setCookies, context.Response.Headers.XFrameOptions.ToString()) { Headers = context.Response.Headers };
	}

	// Signs up a password account through the real sign-up page, which signs it in
	public async Task SignUp(string name, string email)
	{
		PageResponse page = await Get("/users/sign_up");
		Assert.Equal(200, page.Status);
		PageResponse created = await Post("/users/sign_up", new()
		{
			["__go_consent_token"] = AccountsBrowser.Token(page),
			["display_name"] = name,
			["email"] = email,
			["password"] = "a long enough password"
		});
		Assert.Equal(302, created.Status);
	}

	// The antiforgery header and token the staff pages read from the <meta> tags of their host page
	public async Task<Dictionary<string, string>> Csrf()
	{
		PageResponse page = await Get("/admin/reports");
		Assert.Equal(200, page.Status);
		return new Dictionary<string, string> { [AdminArea.Meta(page, "csrf-header")] = AdminArea.Meta(page, "csrf-token") };
	}

	private sealed class TestEnvironment : IWebHostEnvironment
	{
		public string ApplicationName { get; set; } = typeof(PortalSetup).Assembly.GetName().Name!;
		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
		public string EnvironmentName { get; set; } = Environments.Development;
		public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
		public string WebRootPath { get; set; } = AppContext.BaseDirectory;
	}

	// The roles of the signed-in user in the probe database, through the real role table (StaffRoles.PermissionsFor)
	private sealed class ProbeRolesHandler : AuthorizationHandler<PermissionRequirement>
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public ProbeRolesHandler(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
		{
			// a token principal passes on its own claims only (TokenPermissionHandler), as in PermissionHandler
			if (context.User.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == TokenPrincipal.AuthenticationType)
				|| !Int64.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out Int64 userID))
			{
				return;
			}
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			List<string> roles = await db.UserRoles.Where(r => r.UserID == userID).Select(r => r.Role).ToListAsync();
			if (StaffRoles.PermissionsFor(roles).Contains(requirement.Permission))
			{
				context.Succeed(requirement);
			}
		}
	}

	// The token half of PermissionHandler: an API token passes on its own permission claims
	private sealed class TokenPermissionHandler : AuthorizationHandler<PermissionRequirement>
	{
		protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
		{
			if (ApiTokenPrincipals.HasPermission(context.User, requirement.Permission))
			{
				context.Succeed(requirement);
			}
			return Task.CompletedTask;
		}
	}
}

internal static class AdminArea
{
	public static async Task<User> Grant(ProbeDatabase probe, string email, string role, bool twoFactor = true)
	{
		User user = (await PasswordPagesTests.UserByEmail(probe, email))!;
		await using AppDbContext db = probe.Open();
		db.UserRoles.Add(new UserRole { UserID = user.ID, Role = role, Source = AuthoritySources.Local, GrantedAt = DateTime.UtcNow, GrantedBy = "test" });
		// staff password accounts keep two-factor sign-in; the page gate checks it on the account itself
		if (twoFactor)
		{
			await db.Users.Where(u => u.ID == user.ID).ExecuteUpdateAsync(set => set.SetProperty(u => u.TwoFactorEnabled, true));
		}
		await db.SaveChangesAsync();
		return user;
	}

	public static string ErrorOf(PageResponse response) => JsonDocument.Parse(response.Body).RootElement.GetProperty("error").GetString()!;

	public static string Meta(PageResponse page, string name)
	{
		System.Text.RegularExpressions.Match match = Regex.Match(page.Body, "<meta name=\"" + Regex.Escape(name) + "\" content=\"([^\"]*)\"");
		Assert.True(match.Success, "the page has no meta " + name + ": " + page.Body);
		return WebUtility.HtmlDecode(match.Groups[1].Value);
	}
}

// The staff pages under /admin (S12, T14b) calling /api/v1 with the sign-in cookie (T16): a signed-out request and a
// signed-in player are refused, a staff member with the right permission gets in, every change needs the antiforgery header
[Collection(AdminAreaCollection.Name)]
public class AdminAreaTests
{
	private static readonly string[] s_staffReads = ["/api/v1/staff/overview", "/api/v1/reports/search", "/api/v1/players/search?query=ab", "/api/v1/anticheat_reports/modules"];

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task SignedOutIsSentToSignInOrRefused(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AdminBrowser(probe);

		foreach (string page in new[] { "/admin", "/admin/reports", "/admin/players/1", "/admin/anticheat/modules" })
		{
			PageResponse response = await browser.Get(page);
			Assert.Equal(302, response.Status);
			Assert.Equal("http://accounts.test/users/sign_in?ReturnUrl=" + Uri.EscapeDataString(page), response.Location);
		}

		foreach (string api in s_staffReads)
		{
			PageResponse response = await browser.Get(api);
			Assert.Equal(401, response.Status);
			Assert.Equal("unauthorized", AdminArea.ErrorOf(response));
		}
		Assert.Equal(401, (await browser.Patch("/api/v1/reports/1", new { status = "dismissed" })).Status);
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task APlayerIsRefused(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AdminBrowser(probe);
		await browser.SignUp("Plain", "plain@example.org");

		PageResponse page = await browser.Get("/admin/reports");
		Assert.Equal(403, page.Status);
		Assert.Contains("Your account holds no staff permission.", page.Body);
		Assert.DoesNotContain("blazor.web.js", page.Body);
		Assert.DoesNotContain("csrf-token", page.Body);

		foreach (string api in s_staffReads)
		{
			PageResponse response = await browser.Get(api);
			Assert.Equal(403, response.Status);
			Assert.Equal("forbidden", AdminArea.ErrorOf(response));
		}
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffGetInByPermission(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		User warden = await AdminArea.Grant(probe, "warden@example.org", StaffRoles.Moderator);
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		await AdminArea.Grant(probe, "chief@example.org", StaffRoles.Admin);
		// the session meta lists roles and permissions from the process-wide role cache, as the hub reads them
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}

		// the WebAssembly shell, dark only, with the three stylesheets of the sign-in pages in their order
		PageResponse page = await moderator.Get("/admin/reports");
		Assert.Equal(200, page.Status);
		Assert.Contains("<html lang=\"en\" class=\"dark\">", page.Body);
		Assert.Matches(new Regex(Regex.Escape(PortalLook.ThemePath) + ".*" + Regex.Escape(PortalLook.StylesheetPath) + ".*" + Regex.Escape(PortalLook.GeneralsPath), RegexOptions.Singleline), page.Body);
		Assert.Contains("_framework/blazor.web.js", page.Body);
		Assert.DoesNotContain("theme-init.js", page.Body);
		Assert.Equal("DENY", page.FrameOptions);

		// who is signed in comes with the page, the browser asks /api/v1 for everything else
		JsonElement session = JsonDocument.Parse(AdminArea.Meta(page, "go-portal-session")).RootElement;
		Assert.Equal(warden.ID, session.GetProperty("user_id").GetInt64());
		Assert.Equal("Warden", session.GetProperty("display_name").GetString());
		Assert.True(session.GetProperty("staff").GetBoolean());
		Assert.Contains("moderation.kick", session.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
		Assert.NotEmpty(AdminArea.Meta(page, "csrf-token"));

		PageResponse reports = await moderator.Get("/api/v1/reports/search");
		Assert.Equal(200, reports.Status);
		Assert.Equal(0, JsonDocument.Parse(reports.Body).RootElement.GetProperty("reports").GetArrayLength());
		Assert.Equal(200, (await moderator.Get("/api/v1/players/search?query=Chief")).Status);

		// the anti-cheat lists need anticheat.review, which only admin holds
		PageResponse denied = await moderator.Get("/api/v1/anticheat_reports/modules");
		Assert.Equal(403, denied.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(denied));
		Assert.Equal(200, (await admin.Get("/api/v1/anticheat_reports/modules")).Status);
	}

	// a change without the antiforgery header of the host page is refused before it reaches the action
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ChangesNeedTheAntiforgeryHeader(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		await AdminArea.Grant(probe, "warden@example.org", StaffRoles.Moderator);

		PageResponse missing = await moderator.Patch("/api/v1/reports/999", new { status = "dismissed" });
		Assert.Equal(400, missing.Status);
		Assert.Equal("csrf", AdminArea.ErrorOf(missing));

		// past the check, the action itself answers: no such report
		PageResponse unknown = await moderator.Patch("/api/v1/reports/999", new { status = "dismissed" }, await moderator.Csrf());
		Assert.Equal(404, unknown.Status);
	}

	// staff with a password and no second factor set it up before the staff area opens, as before any token
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffWithoutTwoFactorSetItUpFirst(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		await AdminArea.Grant(probe, "warden@example.org", StaffRoles.Moderator, twoFactor: false);

		PageResponse page = await moderator.Get("/admin/reports");
		Assert.Equal(302, page.Status);
		Assert.Equal("/profile/two_factor_auth?ReturnUrl=%2Fadmin%2Freports", page.Location);

		PageResponse reports = await moderator.Get("/api/v1/reports/search");
		Assert.Equal(403, reports.Status);
		Assert.Equal(ApiAuthentication.TwoFactorRequired, AdminArea.ErrorOf(reports));
	}

	// ===== PROBES (verifier), on the /api/v1 routes since T16 =====

	// the rank rule through the cookie: a moderator can neither rename nor sanction an admin, and can rename a player.
	// Uses the process-wide rank cache (StaffRoles.Reload), so it must not run in parallel with other probe tests
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeRankRuleHoldsThroughTheCookie(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		await AdminArea.Grant(probe, "warden@example.org", StaffRoles.Moderator);
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		User chief = await AdminArea.Grant(probe, "chief@example.org", StaffRoles.Admin);
		var player = new AdminBrowser(probe);
		await player.SignUp("Plain", "plain@example.org");
		User plain = (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!;
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}

		Dictionary<string, string> csrf = await moderator.Csrf();
		PageResponse rename = await moderator.Patch($"/api/v1/players/{chief.ID}", new { name = "Renamed", reason = "probe" }, csrf);
		Assert.Equal(403, rename.Status);
		Assert.Equal("moderation.target_outranks", AdminArea.ErrorOf(rename));
		PageResponse mute = await moderator.SendJson("POST", $"/api/v1/players/{chief.ID}/sanctions", new { type = "mute", duration_s = 60, reason = "probe" }, csrf);
		Assert.Equal(403, mute.Status);
		Assert.Equal("moderation.target_outranks", AdminArea.ErrorOf(mute));
		Assert.Equal("Chief", (await PasswordPagesTests.UserByEmail(probe, "chief@example.org"))!.Username);

		// and the same moderator may act on a player
		Assert.Equal(204, (await moderator.Patch($"/api/v1/players/{plain.ID}", new { name = "Cleaned", reason = "probe" }, csrf)).Status);
		Assert.Equal("Cleaned", (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!.Username);
	}

	// IPs and devices need players.inspect: a player and a moderator get 403 and no audit row is written; an admin reads it
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeInspectionNeedsPlayersInspect(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var player = new AdminBrowser(probe);
		await player.SignUp("Plain", "plain@example.org");
		User plain = (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		await AdminArea.Grant(probe, "warden@example.org", StaffRoles.Moderator);
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		await AdminArea.Grant(probe, "chief@example.org", StaffRoles.Admin);

		PageResponse asPlayer = await player.Get($"/api/v1/players/{plain.ID}/inspection");
		Assert.Equal(403, asPlayer.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(asPlayer));
		Assert.Equal(403, (await player.Get($"/api/v1/players/{plain.ID}/staff_view")).Status);
		PageResponse asModerator = await moderator.Get($"/api/v1/players/{plain.ID}/inspection");
		Assert.Equal(403, asModerator.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(asModerator));
		Assert.Contains("players.inspect", asModerator.Body);
		await using (AppDbContext db = probe.Open())
		{
			Assert.False(await db.AuditLog.AnyAsync(a => a.Action == ModerationActions.PlayerInspect));
		}

		Assert.Equal(200, (await moderator.Get($"/api/v1/players/{plain.ID}/staff_view")).Status);
		Assert.Equal(200, (await admin.Get($"/api/v1/players/{plain.ID}/inspection")).Status);
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(1, await db.AuditLog.CountAsync(a => a.Action == ModerationActions.PlayerInspect));
		}
	}

	// every cookie change (POST, PATCH, DELETE) without the header, or with another account's token, is a 400 csrf
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeEveryChangeNeedsThisAccountsToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		await AdminArea.Grant(probe, "chief@example.org", StaffRoles.Admin);
		var other = new AdminBrowser(probe);
		await other.SignUp("Other", "other@example.org");
		User target = await AdminArea.Grant(probe, "other@example.org", StaffRoles.Admin);

		var changes = new (string Method, string Path, object Body)[]
		{
			("POST", $"/api/v1/players/{target.ID}/sanctions", new { type = "mute", duration_s = 60, reason = "probe" }),
			("PATCH", $"/api/v1/players/{target.ID}", new { name = "Renamed", reason = "probe" }),
			("DELETE", "/api/v1/sanctions/1", new { reason = "probe" }),
			("PATCH", "/api/v1/reports/1", new { status = "dismissed" }),
			("PATCH", "/api/v1/anticheat_reports/modules/1", new { reviewed = true })
		};
		Dictionary<string, string> foreign = await other.Csrf();
		foreach ((string method, string path, object body) in changes)
		{
			PageResponse missing = await admin.SendJson(method, path, body);
			Assert.Equal(400, missing.Status);
			Assert.Equal("csrf", AdminArea.ErrorOf(missing));
			PageResponse stolen = await admin.SendJson(method, path, body, foreign);
			Assert.Equal(400, stolen.Status);
			Assert.Equal("csrf", AdminArea.ErrorOf(stolen));
		}
		await using AppDbContext db = probe.Open();
		Assert.Equal("Other", (await db.Users.SingleAsync(u => u.ID == target.ID)).Username);
	}

	// the antiforgery middleware of the staff area leaves the MVC forms and the OAuth endpoints as they were
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeFormsAndOAuthStillWorkBehindUseAntiforgery(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AdminBrowser(probe);
		await browser.SignUp("Before", "before@example.org");

		PageResponse page = await browser.Get("/profile");
		Assert.Equal(200, page.Status);
		Assert.Equal(400, (await browser.Post("/profile", new() { ["display_name"] = "Forged" })).Status);
		PageResponse renamed = await browser.Post("/profile", new() { ["__go_consent_token"] = AccountsBrowser.Token(page), ["display_name"] = "After" });
		Assert.Equal(302, renamed.Status);
		Assert.Equal("/profile?renamed=1", renamed.Location);

		var fresh = new AdminBrowser(probe);
		Assert.Equal(400, (await fresh.Post("/users/sign_in", new() { ["email"] = "before@example.org", ["password"] = "a long enough password" })).Status);
		PageResponse signInPage = await fresh.Get("/users/sign_in");
		PageResponse signedIn = await fresh.Post("/users/sign_in", new() { ["__go_consent_token"] = AccountsBrowser.Token(signInPage), ["email"] = "before@example.org", ["password"] = "a long enough password" });
		Assert.Equal(302, signedIn.Status);

		PageResponse token = await fresh.Post("/oauth/token", new() { ["grant_type"] = "password", ["username"] = "x", ["password"] = "y" });
		Assert.DoesNotContain("\"csrf\"", token.Body);
		Assert.Contains("unsupported_grant_type", token.Body);
	}
	// The staff start page (T17a), GET /api/v1/staff/overview with the sign-in cookie: the numbers and newest rows match the seeded database; the anti-cheat numbers need
	// anticheat.review like the lists, so a moderator gets null where an admin gets the counts
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheOverviewCountsTheSeededRows(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		User warden = await AdminArea.Grant(probe, "warden@example.org", StaffRoles.Moderator);
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		await AdminArea.Grant(probe, "chief@example.org", StaffRoles.Admin);
		var player = new AdminBrowser(probe);
		await player.SignUp("Plain", "plain@example.org");
		User plain = (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!;

		DateTime now = DateTime.UtcNow;
		DateTime dayStart = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
		await using (AppDbContext seed = probe.Open())
		{
			// two finished today, one finished yesterday, one still running
			seed.Matches.AddRange(
				new global::Match { Name = "today 1", StartedAt = dayStart.AddHours(-1), FinishedAt = dayStart },
				new global::Match { Name = "today 2", StartedAt = dayStart, FinishedAt = dayStart.AddSeconds(1) },
				new global::Match { Name = "yesterday", StartedAt = dayStart.AddHours(-3), FinishedAt = dayStart.AddHours(-2) },
				new global::Match { Name = "running", StartedAt = dayStart });
			for (int i = 0; i < 4; i++)
			{
				seed.PlayerReports.Add(new PlayerReport
				{
					ReporterID = warden.ID,
					ReportedUserID = plain.ID,
					Reason = $"Reason {i}",
					Evidence = "{\"messages\":[]}",
					Status = i == 3 ? PlayerReportStatuses.Dismissed : PlayerReportStatuses.Open,
					CreatedAt = now.AddMinutes(-10 + i)
				});
			}
			seed.AnticheatNewAccountGames.AddRange(new AnticheatNewAccountGame { UserId = plain.ID, MatchId = 1 }, new AnticheatNewAccountGame { UserId = plain.ID, MatchId = 2 });
			seed.AnticheatModuleReports.AddRange(
				new AnticheatModuleReport { UserId = plain.ID, MatchId = 1, ModuleName = "inject.dll", ModulePath = "C:\\inject.dll", ModuleSize = 1 },
				new AnticheatModuleReport { UserId = plain.ID, MatchId = 1, ModuleName = "seen.dll", ModulePath = "C:\\seen.dll", ModuleSize = 1, ReviewedAt = now });
			seed.AnticheatProbeReports.Add(new AnticheatProbeReport { UserId = plain.ID, MatchId = 1, Reason = "Malformed probe response" });
			seed.AnticheatProbeReports.Add(new AnticheatProbeReport { UserId = plain.ID, MatchId = 0, Reason = "Spoofed device value" });
			seed.Sanctions.Add(new Sanction { UserID = plain.ID, Type = SanctionTypes.Mute, Reason = "spam", CreatedByKind = AuditActorKinds.User, CreatedByID = warden.ID, CreatedAt = now.AddMinutes(-5), ExpiresAt = now.AddHours(1) });
			seed.Sanctions.Add(new Sanction { UserID = plain.ID, Type = SanctionTypes.Ban, Reason = "cheating", CreatedByKind = AuditActorKinds.User, CreatedByID = warden.ID, CreatedAt = now.AddMinutes(-1) });
			await seed.SaveChangesAsync();
		}

		PageResponse asModerator = await moderator.Get("/api/v1/staff/overview");
		Assert.Equal(200, asModerator.Status);
		JsonElement overview = JsonDocument.Parse(asModerator.Body).RootElement;
		Assert.Equal(0, overview.GetProperty("players_online").GetInt32());
		Assert.Equal(0, overview.GetProperty("lobbies_open").GetInt32());
		Assert.Equal(2, overview.GetProperty("matches_today").GetInt32());
		Assert.Equal(3, overview.GetProperty("open_reports").GetInt32());
		Assert.True(!overview.TryGetProperty("unreviewed_anticheat", out JsonElement hidden) || hidden.ValueKind == JsonValueKind.Null);
		Assert.True(!overview.TryGetProperty("anticheat", out JsonElement hiddenLists) || hiddenLists.ValueKind == JsonValueKind.Null);

		// newest first: the dismissed report was created last
		JsonElement[] reports = overview.GetProperty("recent_reports").EnumerateArray().ToArray();
		Assert.Equal(["Reason 3", "Reason 2", "Reason 1", "Reason 0"], reports.Select(r => r.GetProperty("reason").GetString()));
		Assert.Equal("dismissed", reports[0].GetProperty("status").GetString());
		Assert.Equal("Plain", reports[0].GetProperty("reported").GetProperty("name").GetString());
		Assert.Equal("Warden", reports[0].GetProperty("reporter").GetProperty("name").GetString());

		JsonElement[] sanctions = overview.GetProperty("recent_sanctions").EnumerateArray().ToArray();
		Assert.Equal(["ban", "mute"], sanctions.Select(s => s.GetProperty("type").GetString()));
		Assert.Equal("Plain", sanctions[0].GetProperty("player").GetProperty("name").GetString());
		Assert.Equal("Warden", sanctions[0].GetProperty("created_by").GetProperty("name").GetString());
		Assert.True(sanctions[0].GetProperty("active").GetBoolean());
		Assert.Equal("cheating", sanctions[0].GetProperty("reason").GetString());

		PageResponse asAdmin = await admin.Get("/api/v1/staff/overview");
		Assert.Equal(200, asAdmin.Status);
		JsonElement full = JsonDocument.Parse(asAdmin.Body).RootElement;
		Assert.Equal(5, full.GetProperty("unreviewed_anticheat").GetInt32());
		Assert.Equal(
			[("new_accounts", 2), ("modules", 1), ("probes", 1), ("devices", 1)],
			full.GetProperty("anticheat").EnumerateArray().Select(q => (q.GetProperty("kind").GetString()!, q.GetProperty("unreviewed").GetInt32())));
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task TheOverviewIsStaffOnly(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var signedOut = new AdminBrowser(probe);
		PageResponse anonymous = await signedOut.Get("/api/v1/staff/overview");
		Assert.Equal(401, anonymous.Status);
		Assert.Equal("unauthorized", AdminArea.ErrorOf(anonymous));

		var player = new AdminBrowser(probe);
		await player.SignUp("Plain", "plain@example.org");
		PageResponse refused = await player.Get("/api/v1/staff/overview");
		Assert.Equal(403, refused.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(refused));

		// the start page itself is behind the same gate as every staff page
		PageResponse page = await player.Get("/admin");
		Assert.Equal(403, page.Status);
	}
}
