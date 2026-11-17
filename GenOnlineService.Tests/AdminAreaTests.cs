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
using GenOnlineService.Portal;
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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// The staff pages under /admin and their cookie API under /admin/api (S12, T14b): a signed-out request and a signed-in
// player are refused, a staff member with the right permission gets in, every change needs the antiforgery header
// The rank probe reloads the process-wide StaffRoles cache, so this class runs alone, after the parallel tests
[CollectionDefinition(AdminAreaCollection.Name, DisableParallelization = true)]
public sealed class AdminAreaCollection
{
	public const string Name = "Admin area (process-wide roles)";
}

[Collection(AdminAreaCollection.Name)]
public class AdminAreaTests
{
	// The accounts pages and the staff area in one in-process pipeline, as Program wires them
	private sealed class AdminBrowser
	{
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
			services.AddGenOnlineIdentity(accounts, development: true);
			services.AddRouting();
			services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly);
			// the perm:<name> policies of the host; the handler maps the probe database's roles with the real role table,
			// as the process-wide role cache (StaffRoles) would mix up the user ids of parallel probe databases
			services.AddAuthorization();
			services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
			services.AddSingleton<IAuthorizationHandler, ProbeRolesHandler>();
			PortalSetup.AddPortal(services);
			m_services = services.BuildServiceProvider();

			var app = new ApplicationBuilder(m_services);
			app.UseRouting();
			app.UseAuthentication();
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
			context.Request.Host = new HostString("accounts.test");
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

			return new PageResponse(context.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()), context.Response.Headers.Location.ToString() is { Length: > 0 } l ? l : null, setCookies, context.Response.Headers.XFrameOptions.ToString());
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
			if (!Int64.TryParse(context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out Int64 userID))
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

	private static async Task<User> Grant(ProbeDatabase probe, string email, string role, bool twoFactor = true)
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

	private static string ErrorOf(PageResponse response) => JsonDocument.Parse(response.Body).RootElement.GetProperty("error").GetString()!;

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

		foreach (string api in new[] { "/admin/api/session", "/admin/api/reports", "/admin/api/players?search=ab", "/admin/api/anticheat/modules" })
		{
			PageResponse response = await browser.Get(api);
			Assert.Equal(401, response.Status);
			Assert.Equal("unauthorized", ErrorOf(response));
		}
		Assert.Equal(401, (await browser.Patch("/admin/api/reports/1", new { status = "dismissed" })).Status);
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

		PageResponse reports = await browser.Get("/admin/api/reports");
		Assert.Equal(403, reports.Status);
		Assert.Equal("forbidden", ErrorOf(reports));
		Assert.Equal(403, (await browser.Get("/admin/api/players?search=Plain")).Status);
		Assert.Equal(403, (await browser.Get("/admin/api/anticheat/modules")).Status);

		// the session answer is the signed-in player's own, not a staff one
		PageResponse session = await browser.Get("/admin/api/session");
		Assert.Equal(200, session.Status);
		Assert.False(JsonDocument.Parse(session.Body).RootElement.GetProperty("staff").GetBoolean());
	}

	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task StaffGetInByPermission(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		await Grant(probe, "warden@example.org", StaffRoles.Moderator);
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		await Grant(probe, "chief@example.org", StaffRoles.Admin);

		// the WebAssembly shell, dark only, with the three stylesheets of the sign-in pages in their order
		PageResponse page = await moderator.Get("/admin/reports");
		Assert.Equal(200, page.Status);
		Assert.Contains("<html lang=\"en\" class=\"dark\">", page.Body);
		Assert.Matches(new Regex(Regex.Escape(PortalLook.ThemePath) + ".*" + Regex.Escape(PortalLook.StylesheetPath) + ".*" + Regex.Escape(PortalLook.GeneralsPath), RegexOptions.Singleline), page.Body);
		Assert.Contains("_framework/blazor.web.js", page.Body);
		Assert.DoesNotContain("theme-init.js", page.Body);
		Assert.Equal("DENY", page.FrameOptions);

		PageResponse reports = await moderator.Get("/admin/api/reports");
		Assert.Equal(200, reports.Status);
		Assert.Equal(0, JsonDocument.Parse(reports.Body).RootElement.GetProperty("total").GetInt32());
		Assert.Equal(200, (await moderator.Get("/admin/api/players?search=Chief")).Status);

		// the anti-cheat lists need anticheat.review, which only admin holds
		PageResponse denied = await moderator.Get("/admin/api/anticheat/modules");
		Assert.Equal(403, denied.Status);
		Assert.Equal("forbidden", ErrorOf(denied));
		Assert.Equal(200, (await admin.Get("/admin/api/anticheat/modules")).Status);
	}

	// a change without the antiforgery header of the session answer is refused before it reaches the action
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ChangesNeedTheAntiforgeryHeader(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		await Grant(probe, "warden@example.org", StaffRoles.Moderator);

		PageResponse missing = await moderator.Patch("/admin/api/reports/999", new { status = "dismissed" });
		Assert.Equal(400, missing.Status);
		Assert.Equal("csrf", ErrorOf(missing));

		JsonElement session = JsonDocument.Parse((await moderator.Get("/admin/api/session")).Body).RootElement;
		Assert.True(session.GetProperty("staff").GetBoolean());
		var header = new Dictionary<string, string> { [session.GetProperty("csrf_header").GetString()!] = session.GetProperty("csrf_token").GetString()! };

		// past the check, the action itself answers: no such report
		PageResponse unknown = await moderator.Patch("/admin/api/reports/999", new { status = "dismissed" }, header);
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
		await Grant(probe, "warden@example.org", StaffRoles.Moderator, twoFactor: false);

		PageResponse page = await moderator.Get("/admin/reports");
		Assert.Equal(302, page.Status);
		Assert.Equal("/profile/two_factor_auth?ReturnUrl=%2Fadmin%2Freports", page.Location);

		PageResponse reports = await moderator.Get("/admin/api/reports");
		Assert.Equal(403, reports.Status);
		Assert.Equal(PortalSetup.TwoFactorRequired, ErrorOf(reports));
	}
	// ===== PROBES (verifier) =====
	private static async Task<Dictionary<string, string>> CsrfOf(AdminBrowser browser)
	{
		JsonElement session = JsonDocument.Parse((await browser.Get("/admin/api/session")).Body).RootElement;
		return new Dictionary<string, string> { [session.GetProperty("csrf_header").GetString()!] = session.GetProperty("csrf_token").GetString()! };
	}

	// the rank rule through /admin/api: a moderator can neither rename nor sanction an admin, and can rename a player.
	// Uses the process-wide rank cache (StaffRoles.Reload), so it must not run in parallel with other probe tests
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeRankRuleHoldsThroughAdminApi(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		await moderator.SignUp("Warden", "warden@example.org");
		await Grant(probe, "warden@example.org", StaffRoles.Moderator);
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		User chief = await Grant(probe, "chief@example.org", StaffRoles.Admin);
		var player = new AdminBrowser(probe);
		await player.SignUp("Plain", "plain@example.org");
		User plain = (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!;
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}

		Dictionary<string, string> csrf = await CsrfOf(moderator);
		PageResponse rename = await moderator.Patch($"/admin/api/players/{chief.ID}", new { name = "Renamed", reason = "probe" }, csrf);
		Assert.Equal(403, rename.Status);
		Assert.Equal("moderation.target_outranks", ErrorOf(rename));
		PageResponse mute = await moderator.SendJson("POST", $"/admin/api/players/{chief.ID}/sanctions", new { type = "mute", duration_s = 60, reason = "probe" }, csrf);
		Assert.Equal(403, mute.Status);
		Assert.Equal("moderation.target_outranks", ErrorOf(mute));
		Assert.Equal("Chief", (await PasswordPagesTests.UserByEmail(probe, "chief@example.org"))!.Username);

		// and the same moderator may act on a player
		Assert.Equal(204, (await moderator.Patch($"/admin/api/players/{plain.ID}", new { name = "Cleaned", reason = "probe" }, csrf)).Status);
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
		await Grant(probe, "warden@example.org", StaffRoles.Moderator);
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		await Grant(probe, "chief@example.org", StaffRoles.Admin);

		PageResponse asPlayer = await player.Get($"/admin/api/players/{plain.ID}/inspection");
		Assert.Equal(403, asPlayer.Status);
		Assert.Equal("forbidden", ErrorOf(asPlayer));
		Assert.Equal(403, (await player.Get($"/admin/api/players/{plain.ID}")).Status);
		PageResponse asModerator = await moderator.Get($"/admin/api/players/{plain.ID}/inspection");
		Assert.Equal(403, asModerator.Status);
		Assert.Equal("forbidden", ErrorOf(asModerator));
		Assert.Contains("players.inspect", asModerator.Body);
		await using (AppDbContext db = probe.Open())
		{
			Assert.False(await db.AuditLog.AnyAsync(a => a.Action == ModerationActions.PlayerInspect));
		}

		Assert.Equal(200, (await admin.Get($"/admin/api/players/{plain.ID}/inspection")).Status);
		await using (AppDbContext db = probe.Open())
		{
			Assert.Equal(1, await db.AuditLog.CountAsync(a => a.Action == ModerationActions.PlayerInspect));
		}
	}

	// every change (POST, PATCH, DELETE) without the header, or with another account's token, is a 400 csrf
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeEveryChangeNeedsThisAccountsToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var admin = new AdminBrowser(probe);
		await admin.SignUp("Chief", "chief@example.org");
		await Grant(probe, "chief@example.org", StaffRoles.Admin);
		var other = new AdminBrowser(probe);
		await other.SignUp("Other", "other@example.org");
		User target = await Grant(probe, "other@example.org", StaffRoles.Admin);

		var changes = new (string Method, string Path, object Body)[]
		{
			("POST", $"/admin/api/players/{target.ID}/sanctions", new { type = "mute", duration_s = 60, reason = "probe" }),
			("PATCH", $"/admin/api/players/{target.ID}", new { name = "Renamed", reason = "probe" }),
			("DELETE", "/admin/api/sanctions/1", new { reason = "probe" }),
			("PATCH", "/admin/api/reports/1", new { status = "dismissed" }),
			("PATCH", "/admin/api/anticheat/modules/1", new { reviewed = true })
		};
		Dictionary<string, string> foreign = await CsrfOf(other);
		foreach ((string method, string path, object body) in changes)
		{
			PageResponse missing = await admin.SendJson(method, path, body);
			Assert.Equal(400, missing.Status);
			Assert.Equal("csrf", ErrorOf(missing));
			PageResponse stolen = await admin.SendJson(method, path, body, foreign);
			Assert.Equal(400, stolen.Status);
			Assert.Equal("csrf", ErrorOf(stolen));
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
}
