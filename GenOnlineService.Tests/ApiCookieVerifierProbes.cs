using GenOnlineService.Api;
using GenOnlineService.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GenOnlineService.Tests;

// Verifier probes for T16: the Origin rule on changes, no fallback to the cookie behind any Authorization header, an
// anonymous antiforgery token refused, two-factor on changes, audit source api and one inspection row per window
[Collection(AdminAreaCollection.Name)]
public class ApiCookieVerifierProbes
{
	private static async Task<(AdminBrowser Browser, User User)> StaffAsync(ProbeDatabase probe, string name, string role, bool twoFactor = true)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (browser, await AdminArea.Grant(probe, email, role, twoFactor));
	}

	private static async Task<User> PlayerAsync(ProbeDatabase probe, string name)
	{
		var browser = new AdminBrowser(probe);
		string email = name.ToLowerInvariant() + "@example.org";
		await browser.SignUp(name, email);
		return (await PasswordPagesTests.UserByEmail(probe, email))!;
	}

	// a change with this user's token: no Origin passes (a non-browser or an old browser), a foreign Origin never does
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeAForeignOriginChangeIsRefusedEvenWithTheToken(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		User plain = await PlayerAsync(probe, "Plain");
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}
		Dictionary<string, string> csrf = await moderator.Csrf();

		var foreign = new Dictionary<string, string>(csrf) { ["Origin"] = "https://evil.example" };
		PageResponse refused = await moderator.Patch($"/api/v1/players/{plain.ID}", new { name = "Evil", reason = "probe" }, foreign);
		Assert.Equal(403, refused.Status);
		Assert.Equal("forbidden", AdminArea.ErrorOf(refused));
		Assert.Equal("Plain", (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!.Username);

		var same = new Dictionary<string, string>(csrf) { ["Origin"] = "http://" + AdminBrowser.Host };
		Assert.Equal(204, (await moderator.Patch($"/api/v1/players/{plain.ID}", new { name = "Cleaned", reason = "probe" }, same)).Status);
		Assert.Equal(204, (await moderator.Patch($"/api/v1/players/{plain.ID}", new { name = "Again", reason = "probe" }, csrf)).Status);
	}

	// any Authorization header keeps the bearer path, so an unknown or malformed one is a 401 next to a valid cookie
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeAnyAuthorizationHeaderNeverFallsBackToTheCookie(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, _) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);

		foreach (string header in new[] { "Bearer nonsense", "Basic dXNlcjpwYXNz", "Bearer", "gopat-unknown" })
		{
			PageResponse response = await moderator.Get("/api/v1/players/search?query=Warden", new() { ["Authorization"] = header });
			Assert.Equal(401, response.Status);
			Assert.Equal("unauthorized", AdminArea.ErrorOf(response));
		}
		Assert.Equal(200, (await moderator.Get("/api/v1/players/search?query=Warden")).Status);
	}

	// a token issued before sign-in (bound to nobody) is not this user's token
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeAnAnonymousTokenIsRefused(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var moderator = new AdminBrowser(probe);
		string anonymous = AccountsBrowser.Token(await moderator.Get("/users/sign_up"));
		await moderator.SignUp("Warden", "warden@example.org");
		await AdminArea.Grant(probe, "warden@example.org", StaffRoles.Moderator);
		User plain = await PlayerAsync(probe, "Plain");
		string header = (await moderator.Csrf()).Keys.Single();

		PageResponse response = await moderator.Patch($"/api/v1/players/{plain.ID}", new { name = "Cleaned", reason = "probe" }, new() { [header] = anonymous });
		Assert.Equal(400, response.Status);
		Assert.Equal(ApiAuthentication.CsrfError, AdminArea.ErrorOf(response));
	}

	// a staff password account without two-factor sign-in cannot change anything either, even holding a valid token
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeTwoFactorAppliesToChanges(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser moderator, User warden) = await StaffAsync(probe, "Warden", StaffRoles.Moderator);
		User plain = await PlayerAsync(probe, "Plain");
		// a valid header token taken while two-factor was on, then two-factor is turned off on the account
		Dictionary<string, string> csrf = await moderator.Csrf();
		await using (AppDbContext db = probe.Open())
		{
			await db.Users.Where(u => u.ID == warden.ID).ExecuteUpdateAsync(set => set.SetProperty(u => u.TwoFactorEnabled, false));
		}

		PageResponse response = await moderator.Patch($"/api/v1/players/{plain.ID}", new { name = "Cleaned", reason = "probe" }, csrf);
		Assert.Equal(403, response.Status);
		Assert.Equal(ApiAuthentication.TwoFactorRequired, AdminArea.ErrorOf(response));
		Assert.Equal("Plain", (await PasswordPagesTests.UserByEmail(probe, "plain@example.org"))!.Username);
	}

	// cookie changes and inspections are audited as the staff member through the api source; inspection once per window
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task ProbeCookieActionsAreAuditedAsApi(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		(AdminBrowser admin, User chief) = await StaffAsync(probe, "Chief", StaffRoles.Admin);
		User plain = await PlayerAsync(probe, "Plain");
		await using (AppDbContext db = probe.Open())
		{
			await StaffRoles.Reload(db);
		}
		Dictionary<string, string> csrf = await admin.Csrf();

		Assert.Equal(200, (await admin.Get($"/api/v1/players/{plain.ID}/inspection")).Status);
		Assert.Equal(200, (await admin.Get($"/api/v1/players/{plain.ID}/inspection")).Status);
		Assert.Equal(200, (await admin.SendJson("POST", $"/api/v1/players/{plain.ID}/sanctions", new { type = "mute", duration_s = 60, reason = "probe" }, csrf)).Status);
		Assert.Equal(204, (await admin.Patch($"/api/v1/players/{plain.ID}", new { name = "Cleaned", reason = "probe" }, csrf)).Status);

		await using (AppDbContext db = probe.Open())
		{
			List<AuditLogEntry> rows = await db.AuditLog.Where(a => a.TargetID == plain.ID).ToListAsync();
			Assert.Equal(1, rows.Count(a => a.Action == ModerationActions.PlayerInspect));
			Assert.True(rows.Count >= 3, String.Join(", ", rows.Select(r => r.Action)));
			Assert.All(rows, row =>
			{
				Assert.Equal(ModerationSources.Api, row.Source);
				Assert.Equal(chief.ID, row.ActorID);
			});
		}
	}
}
