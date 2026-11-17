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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GenOnlineService.Tests;

// The sign-in pages in the portal look (Blazor Blueprint's prebuilt stylesheet, no Tailwind build): every class the pages
// use must exist in the stylesheet the package ships, so a library update that drops one fails here instead of in a browser
public class PortalLookTests
{
	// A served path (/portal/theme.css, /_content/...) to the file behind it, through the static web assets manifest
	private static string FileOf(string servedPath)
	{
		using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GenOnlineService.staticwebassets.runtime.json")));
		JsonElement node = manifest.RootElement.GetProperty("Root");
		foreach (string part in servedPath.TrimStart('/').Split('/'))
		{
			Assert.True(node.TryGetProperty("Children", out JsonElement children) && children.ValueKind == JsonValueKind.Object && children.TryGetProperty(part, out node),
				$"{servedPath} is not a static web asset of the service");
		}
		JsonElement asset = node.GetProperty("Asset");
		string root = manifest.RootElement.GetProperty("ContentRoots")[asset.GetProperty("ContentRootIndex").GetInt32()].GetString()!;
		return Path.Combine(root, asset.GetProperty("SubPath").GetString()!);
	}

	// The selector the stylesheet writes for a class: every character but letters, digits, '-' and '_' escaped
	private static string Selector(string className)
	{
		var selector = new StringBuilder(".");
		foreach (char c in className)
		{
			selector.Append(Char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' ? c.ToString() : "\\" + c);
		}
		return selector.ToString();
	}

	private static IEnumerable<string> ClassesIn(string html) =>
		Regex.Matches(html, "class=\"([^\"]*)\"").SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(c => c.StartsWith("bb:", StringComparison.Ordinal));

	private static void AssertStyled(string html)
	{
		string css = File.ReadAllText(FileOf(PortalLook.StylesheetPath));
		string[] missing = ClassesIn(html).Distinct().Where(c => !css.Contains(Selector(c), StringComparison.Ordinal)).ToArray();
		Assert.True(missing.Length == 0, "not in the shipped blazorblueprint.css: " + String.Join(", ", missing));
	}

	[Fact]
	public void TheStylesheetThemeAndScriptAreServed()
	{
		Assert.True(File.Exists(FileOf(PortalLook.ThemePath)));
		Assert.True(File.Exists(FileOf(PortalLook.StylesheetPath)));
		Assert.True(File.Exists(FileOf(PortalLook.GeneralsPath)));
	}

	// dark only, and every go-* class the pages use is defined in generals.css
	[Fact]
	public void ThePagesAreDarkWithTheGeneralsLook()
	{
		string page = PortalLook.Document("Title", PortalLook.Text("t"));
		Assert.Contains("<html lang=\"en\" class=\"dark\">", page);
		Assert.DoesNotContain("<script", page);

		string generals = File.ReadAllText(FileOf(PortalLook.GeneralsPath));
		foreach (string goClass in Regex.Matches(page, "class=\"([^\"]*)\"").SelectMany(m => m.Groups[1].Value.Split(' ')).Where(c => c.StartsWith("go-", StringComparison.Ordinal)).Distinct())
		{
			Assert.Contains("." + goClass + " {", generals);
		}
	}

	[Fact]
	public void EveryHelperClassIsInTheStylesheet()
	{
		string page = PortalLook.Document("Title", PortalLook.Text("t") + PortalLook.Field("f", "F", "type=\"text\"", "v") + PortalLook.Submit("s")
			+ PortalLook.Submit("o", "n", "v", outline: true) + PortalLook.LinkButton("/", "l") + PortalLook.LinkButton("/", "p", outline: false)
			+ PortalLook.Link("/", "k") + PortalLook.Alert("a") + PortalLook.Alert("b", success: true) + PortalLook.Separator());

		Assert.Contains($"href=\"{PortalLook.ThemePath}\"", page);
		Assert.Contains($"href=\"{PortalLook.StylesheetPath}\"", page);
		Assert.Contains($"href=\"{PortalLook.GeneralsPath}\"", page);
		AssertStyled(page);
	}

	// the pages themselves, with the classes the controller adds around the helpers
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EveryPageClassIsInTheStylesheet(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled(("Accounts:providers:discord:enabled", "true"),
			("Accounts:providers:discord:client_id", "d-id"), ("Accounts:providers:discord:client_secret", "d-secret")));

		PageResponse signIn = await browser.Get("/users/sign_in");
		Assert.Equal(200, signIn.Status);
		Assert.Contains("href=\"/users/auth/discord?returnUrl=", signIn.Body);
		AssertStyled(signIn.Body);
		AssertStyled((await browser.Get("/users/sign_up")).Body);

		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Styled", "styled@example.org", "a long enough password")).Status);
		AssertStyled((await browser.Get("/profile/two_factor_auth")).Body);
		await using (AppDbContext db = probe.Open())
		{
			User user = db.Users.AsTracking().Single(u => u.Email == "styled@example.org");
			user.NameRequiredAt = DateTime.UtcNow;
			await db.SaveChangesAsync();
		}
		AssertStyled((await browser.Get("/users/sign_up/welcome")).Body);
	}
}
