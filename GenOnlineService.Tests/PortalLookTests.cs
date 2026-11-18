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

// The sign-in and account pages on the Generals Online design system (tokens.css, the gp-* classes of generals.css): every
// class the pages use must be defined, the files they link must be served and cached, and no page may reach another host.
// The staff pages keep Blazor Blueprint's stylesheet; their test is below and proves they still find every class.
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

	private static IEnumerable<string> ClassesOf(string html, string prefix) =>
		Regex.Matches(html, "class=\"([^\"]*)\"").SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(c => c.StartsWith(prefix, StringComparison.Ordinal)).Distinct();

	// every gp-* class a page uses has a rule in generals.css
	private static void AssertGpClassesDefined(string page)
	{
		string generals = File.ReadAllText(FileOf(PortalLook.GeneralsPath));
		string[] missing = ClassesOf(page, "gp").Where(c => !Regex.IsMatch(generals, Regex.Escape("." + c) + @"[ :{,>\[.]")).ToArray();
		Assert.True(missing.Length == 0, "not in generals.css: " + String.Join(", ", missing));
	}

	// a page reaches no other host: no absolute address in a src, href or action, and no font host
	private static void AssertSelfContained(string page)
	{
		Assert.DoesNotMatch("(src|href|action)=\"(https?:)?//", Regex.Replace(page, "<a [^>]*rel=\"noopener noreferrer\"[^>]*>", String.Empty));
		Assert.DoesNotContain("googleapis", page);
		Assert.DoesNotContain("gstatic", page);
		Assert.DoesNotContain("<script", page);
	}

	[Fact]
	public void TheStylesheetsFontsAndImagesAreServed()
	{
		foreach (string path in new[] { PortalLook.TokensPath, PortalLook.GeneralsPath, PortalLook.LogoPath, PortalLook.GameReplaysLogoPath, PortalLook.ThemePath, PortalLook.StylesheetPath }.Concat(AccountsSettings.DefaultCoverImages))
		{
			Assert.True(File.Exists(FileOf(path)), path);
		}
		// every font the stylesheet names is a file of the service
		string generals = File.ReadAllText(FileOf(PortalLook.GeneralsPath));
		string[] fonts = Regex.Matches(generals, @"url\(""(/portal/fonts/[^""]+\.woff2)""\)").Select(m => m.Groups[1].Value).Distinct().ToArray();
		Assert.Contains(fonts, f => f.Contains("Play-latin-700"));
		Assert.Contains(fonts, f => f.Contains("IBMPlexSans-cyrillic-400"));
		Assert.Contains(fonts, f => f.Contains("IBMPlexSansArabic-arabic-700"));
		foreach (string font in fonts)
		{
			Assert.True(File.Exists(FileOf(font)), font);
		}
		Assert.DoesNotContain("http", generals.Substring(generals.IndexOf("The sign-in and account pages on the Generals Online design system", StringComparison.Ordinal)).Split("*/")[1]);
	}

	// the design's tokens, linked by the pages, and no colour of our own in the new rules but the four provider brands'
	[Fact]
	public void TheNewRulesUseTheTokens()
	{
		string tokens = File.ReadAllText(FileOf(PortalLook.TokensPath));
		Assert.Contains("--go-color-accent-default: #E3BA6C;", tokens);
		Assert.Contains("--go-color-border-focus", tokens);
		string generals = File.ReadAllText(FileOf(PortalLook.GeneralsPath));
		string mine = generals[generals.IndexOf("The sign-in and account pages on the Generals Online design system", StringComparison.Ordinal)..];
		foreach (string line in mine.Split('\n').Where(l => !l.Contains("gp-prov-", StringComparison.Ordinal)))
		{
			Assert.DoesNotMatch("#[0-9a-fA-F]{3,8}\\b", line);
		}
		Assert.Matches(@"\.gp-cover\s*\{[^}]*filter: grayscale\(1\);[^}]*opacity: 0\.45", mine);
		Assert.Matches(@"\.gp :focus-visible[^{]*\{\s*outline: 2px solid var\(--go-color-accent-hover\);\s*outline-offset: 3px", mine);
		Assert.Contains("prefers-reduced-motion: reduce", mine);
	}

	private static readonly (string, string, string)[] s_providers = [("/a", "discord", "Discord"), ("/b", "steam", "Steam"), ("/c", "google", "Google"), ("/d", "gamereplays", "GameReplays")];

	// The pages read right to left in Arabic: their own rules place things by the start and end of a line, never by left and
	// right, and arrows mirror
	[Fact]
	public void TheLayoutFollowsTheReadingDirection()
	{
		string css = File.ReadAllText(FileOf(PortalLook.GeneralsPath));
		string mine = css[css.IndexOf("The sign-in and account pages on the Generals Online design system", StringComparison.Ordinal)..];
		foreach (System.Text.RegularExpressions.Match rule in Regex.Matches(mine, @"[^{}]+\{([^}]*)\}"))
		{
			Assert.DoesNotMatch(@"(^|[\s;-])(left|right)\s*:|-(left|right)\s*:|text-align:\s*(left|right)", rule.Groups[1].Value);
		}
		Assert.Matches(@"\[dir=""rtl""\]\s*\.gp-directional\s*\{\s*transform:\s*scaleX\(-1\)", css);
	}

	// Every file the pages link has a fingerprinted address that the static assets serve for a year without a check; the
	// plain addresses are revalidated, so a page linking them made the browser ask again on later visits. The faction art
	// is cached like the rest.
	[Fact]
	public void TheFilesAreLinkedByAddressesCachedForAYear()
	{
		using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(PortalAssets.ManifestPath));
		foreach (string path in new[] { PortalLook.TokensPath, PortalLook.GeneralsPath, PortalLook.LogoPath, PortalLook.GameReplaysLogoPath }.Concat(AccountsSettings.DefaultCoverImages))
		{
			string url = PortalAssets.Url(path);
			string extension = Path.GetExtension(path);
			Assert.Matches("^" + Regex.Escape(path[..^extension.Length]) + @"\.[a-z0-9]{10}" + Regex.Escape(extension) + "$", url);
			JsonElement endpoint = manifest.RootElement.GetProperty("Endpoints").EnumerateArray()
				.First(e => e.GetProperty("Route").GetString() == url.TrimStart('/') && e.GetProperty("Selectors").GetArrayLength() == 0);
			Assert.Contains(endpoint.GetProperty("ResponseHeaders").EnumerateArray(),
				h => h.GetProperty("Name").GetString() == "Cache-Control" && h.GetProperty("Value").GetString() == "max-age=31536000, immutable");
		}
	}

	[Fact]
	public void WithoutTheManifestTheAddressesArePlain()
	{
		Assert.Empty(PortalAssets.Load(Path.Combine(AppContext.BaseDirectory, "no-such.staticwebassets.endpoints.json")));
	}

	private static string Page(PortalSite? site = null, PortalLanguage? language = null, string? icon = null, int? step = null)
		=> PortalLook.Document("Title", PortalLook.Text("t") + PortalLook.Field("f", "F", "type=\"text\"", "v", suffix: "#0042", hint: "h") + PortalLook.Submit("s")
			+ PortalLook.Separator("or") + PortalLook.Providers(s_providers) + PortalLook.Alert("a") + PortalLook.Alert("b", success: true), "Description", language, site, icon, step);

	// the shell of every page: dark, the tokens before generals.css and nothing else, the skip link, the logo with its alt
	// text, the providers in their brands' colours with their marks, and no external address
	[Fact]
	public void ThePagesCarryTheSharedShell()
	{
		var language = new PortalLanguage(System.Globalization.CultureInfo.GetCultureInfo("en"), "/users/sign_in");
		foreach (string page in new[] { Page(null, language), Page(null, language, "mail"), Page(null, language, null, 1) })
		{
			Assert.StartsWith("<!doctype html><html lang=\"en\" dir=\"ltr\">", page);
			Assert.Matches("<link rel=\"stylesheet\" href=\"" + Regex.Escape(PortalAssets.Url(PortalLook.TokensPath)) + "\"><link rel=\"stylesheet\" href=\"" + Regex.Escape(PortalAssets.Url(PortalLook.GeneralsPath)) + "\">", page);
			Assert.DoesNotContain(PortalLook.StylesheetPath, page);
			Assert.DoesNotContain(PortalLook.ThemePath, page);
			Assert.Contains("<body class=\"gp\">", page);
			Assert.Contains("class=\"gp-skip\"", page);
			Assert.Contains($"<img src=\"{PortalAssets.Url(PortalLook.LogoPath)}\" alt=\"Generals Online\">", page);
			Assert.Contains("Sign in with Google</span>", page);
			Assert.Contains("fill=\"#4285F4\"", page); // Google's own colours
			Assert.Contains($"src=\"{PortalAssets.Url(PortalLook.GameReplaysLogoPath)}\"", page);
			Assert.Contains("gp-prov gp-prov-discord", page);
			Assert.Contains("<span id=\"f-suffix\" class=\"gp-suffix\" dir=\"ltr\">#0042</span>", page);
			Assert.Contains("aria-describedby=\"f-suffix f-hint\"", page);
			AssertGpClassesDefined(page);
			AssertSelfContained(page);
		}
	}

	// the cover: the faction art by default, one per visit, grey and dark under the vignette by the stylesheet; a configured
	// list in its place; none, and the plain dark ground
	[Fact]
	public void TheCoverIsTheDefaultArtAConfiguredListOrNothing()
	{
		var defaults = new PortalSite(AccountsSettings.DefaultCoverImages, null, null, null);
		var seen = new HashSet<string>();
		for (int visit = 0; visit < 60; visit++)
		{
			string src = Regex.Match(Page(defaults), "<img class=\"gp-cover\" src=\"([^\"]+)\" alt=\"\" aria-hidden=\"true\"").Groups[1].Value;
			Assert.Contains(AccountsSettings.DefaultCoverImages, d => PortalAssets.Url(d) == src);
			seen.Add(src);
		}
		Assert.True(seen.Count > 1, "a visit picks one of the images at random");

		string configured = Page(new PortalSite(["/art/one.webp"], null, null, null));
		Assert.Contains("<img class=\"gp-cover\" src=\"/art/one.webp\"", configured);
		Assert.DoesNotContain("faction-", configured);

		string none = Page(new PortalSite([], null, null, null));
		Assert.DoesNotContain("gp-cover", none);
		Assert.Contains("class=\"gp-vignette\"", none);
		Assert.DoesNotContain("gp-cover", Page());
	}

	// the footer links the documents that have an address, and nothing else: Help has no page yet
	[Fact]
	public void TheFooterLinksOnlyTheDocumentsThatAreConfigured()
	{
		Assert.DoesNotContain("<nav", Page(new PortalSite([], null, null, null)));
		Assert.DoesNotContain("<footer", Page(new PortalSite([], null, null, null)));

		string some = Page(new PortalSite([], "https://example.org/terms", null, "https://example.org/rules"));
		Assert.Contains("<a href=\"https://example.org/terms\" target=\"_blank\" rel=\"noopener noreferrer\">Terms of Service</a>", some);
		Assert.Contains("<a href=\"https://example.org/rules\" target=\"_blank\" rel=\"noopener noreferrer\">Rules of Engagement</a>", some);
		Assert.DoesNotContain("Privacy Policy", some);

		string all = Page(new PortalSite([], "/t", "/p", "/r"));
		Assert.Contains(">Privacy Policy</a>", all);
		Assert.DoesNotContain(">Help<", all);
	}

	// the language menu: every language in its own name, the page's own marked current, each a link that comes back to the page
	[Fact]
	public void TheLanguageMenuListsEveryLanguageAndComesBack()
	{
		string page = Page(null, new PortalLanguage(System.Globalization.CultureInfo.GetCultureInfo("de"), "/users/sign_up?x=1"));
		Assert.Contains("<summary aria-label=\"Sprache: Deutsch\">", page);
		foreach ((string culture, string name) in new[] { ("en", "English"), ("de", "Deutsch"), ("ru", "Русский"), ("ar", "العربية") })
		{
			Assert.Contains($"<a href=\"/users/language?culture={culture}&amp;ReturnUrl=%2Fusers%2Fsign_up%3Fx%3D1\" lang=\"{culture}\" hreflang=\"{culture}\"{(culture == "de" ? " aria-current=\"true\"" : String.Empty)}>{name}</a>", page);
		}
		// without a language (the staff pages' refusals) there is no menu
		Assert.DoesNotContain("gp-lang", Page());
	}

	// Arabic reads right to left, and says so on the page
	[Fact]
	public void ArabicIsRightToLeft()
	{
		string page = Page(null, new PortalLanguage(System.Globalization.CultureInfo.GetCultureInfo("ar"), "/users/sign_in"));
		Assert.StartsWith("<!doctype html><html lang=\"ar\" dir=\"rtl\">", page);
		Assert.Contains("<a href=\"/users/language?culture=ar", page);
	}

	// every icon the pages name is drawn
	[Fact]
	public void EveryIconThePagesNameExists()
	{
		string[] names = ["arrow-left", "arrow-right", "check", "chevron-down", "circle-alert", "circle-check", "clock", "construction", "gavel", "globe", "id-card", "info", "key-round", "layout-dashboard",
			"log-in", "log-out", "mail", "message-square", "shield-alert", "shield-check", "shield-off", "smartphone", "swords", "triangle-alert", "user", "user-x", "users"];
		foreach (string name in names)
		{
			Assert.True(PortalLook.Icon(name).Length > 0, $"no Lucide icon named {name}");
		}
	}

	[Fact]
	public void EveryHelperClassIsDefined()
	{
		string page = PortalLook.Document("Title", PortalLook.Text("t") + PortalLook.Muted("m") + PortalLook.Field("f", "F", "type=\"text\"", "v", suffix: "#1", hint: "h", aside: "<a href=\"/\">x</a>", large: true, code: true)
			+ PortalLook.Field("g", "G", "type=\"text\"", error: "bad") + PortalLook.Submit("s") + PortalLook.Submit("o", "n", "v", outline: true, large: true, fill: true) + PortalLook.LinkButton("/", "l")
			+ PortalLook.LinkButton("/", "p", outline: false, narrow: true) + PortalLook.GhostLink("/", "g") + PortalLook.Disabled("d") + PortalLook.Actions(PortalLook.GhostLink("/", "b"), PortalLook.Submit("go", narrow: true))
			+ PortalLook.Providers([.. s_providers, ("/e", "other", "Other")]) + PortalLook.Link("/", "k", "lead") + PortalLook.BackLink("/", "back") + PortalLook.Way("/", "way") + PortalLook.WayOn("/", "on", "x")
			+ PortalLook.Alert("a") + PortalLook.Alert("b", success: true) + PortalLook.Separator() + PortalLook.Separator("or") + PortalLook.Badge("b") + PortalLook.Badge("m", muted: true)
			+ PortalLook.Row("r", PortalLook.ProviderName("discord", "Discord")) + PortalLook.Card("T", "D", "c", "f"), "Description", null, new PortalSite(["/a.webp"], "/t", "/p", "/r"), "mail");
		AssertGpClassesDefined(page);
		string app = PortalLook.AppDocument("Profile", new AccountShell("Kwai", "#0042", "7", "player", true, "<form><button type=\"submit\">Sign out</button></form>"), [("/profile", "Profile", "user")], "/profile", "<p>b</p>",
			new PortalLanguage(System.Globalization.CultureInfo.GetCultureInfo("en"), "/profile"), new PortalSite([], "/t", null, null));
		AssertGpClassesDefined(app);
		Assert.Contains("<a href=\"/profile\" aria-current=\"page\">", app);
		Assert.Contains("<a href=\"/admin\">", app);
		AssertSelfContained(app);
	}

	// The staff pages (GenOnlineService.Portal.Client) use the same prebuilt stylesheet and generals.css: every bb: and go-*
	// class literal in their Razor and C# sources must exist there, as the pages run in the browser where no test sees them
	[Fact]
	public void EveryStaffPageClassIsInTheStylesheets()
	{
		string css = File.ReadAllText(FileOf(PortalLook.StylesheetPath));
		string generals = File.ReadAllText(FileOf(PortalLook.GeneralsPath));
		DirectoryInfo root = new DirectoryInfo(AppContext.BaseDirectory);
		while (root.Parent != null && !File.Exists(Path.Combine(root.FullName, "GenServices.sln")))
		{
			root = root.Parent;
		}
		string client = Path.Combine(root.FullName, "GenOnlineService.Portal.Client");
		Assert.True(Directory.Exists(client), "the client sources are next to the solution");

		var missing = new List<string>();
		IEnumerable<string> sources = Directory.EnumerateFiles(client, "*.razor", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories))
			.Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
		foreach (string file in sources)
		{
			string text = File.ReadAllText(file);
			// every bb: word wherever it stands (only classes use the prefix), and every go- word inside a class or Class
			// attribute (go- also starts other names, such as the go-portal-session meta tag); a word joined to a Razor
			// expression (go-x@(...)) is left to the browser check, as its full name exists only at run time
			IEnumerable<string> bbWords = Regex.Matches(text, @"(?<![\w-])bb:[^\s""'<>@]+").Select(m => m.Value);
			IEnumerable<string> goWords = Regex.Matches(text, @"\b[Cc]lass=""([^""]*)""").SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				.Where(word => Regex.IsMatch(word, "^go-[a-z0-9-]*[a-z0-9]$"));
			foreach (string token in bbWords.Concat(goWords).Distinct())
			{
				if (token.StartsWith("bb:", StringComparison.Ordinal) && !css.Contains(Selector(token), StringComparison.Ordinal))
				{
					missing.Add(Path.GetFileName(file) + ": " + token);
				}
				else if (token.StartsWith("go-", StringComparison.Ordinal) && !Regex.IsMatch(generals, Regex.Escape("." + token) + @"[ :{,>\[\.]"))
				{
					missing.Add(Path.GetFileName(file) + ": " + token);
				}
			}
		}
		Assert.True(missing.Count == 0, "not in blazorblueprint.css or generals.css: " + String.Join(", ", missing.Distinct()));
	}

	// the pages themselves, with the classes the controller adds around the helpers
	[ProbeTheory]
	[MemberData(nameof(ProbeDatabase.Providers), MemberType = typeof(ProbeDatabase))]
	public async Task EveryPageIsOnTheDesignSystem(string provider)
	{
		await using ProbeDatabase? probe = await ProbeDatabase.Create(provider);
		if (probe == null) return;
		var browser = new AccountsBrowser(probe, PasswordPagesTests.Enabled(("Accounts:providers:discord:enabled", "true"),
			("Accounts:providers:discord:client_id", "d-id"), ("Accounts:providers:discord:client_secret", "d-secret"),
			("Agreements:terms:version", "1"), ("Agreements:terms:url", "https://example.org/terms"), ("Agreements:privacy:url", "https://example.org/privacy")));

		void AssertPage(PageResponse page)
		{
			Assert.True(page.Body.Length > 0, $"status {page.Status}, location {page.Location}");
			Assert.StartsWith("<!doctype html><html lang=\"en\" dir=\"ltr\">", page.Body);
			Assert.Contains($"href=\"{PortalAssets.Url(PortalLook.TokensPath)}\"", page.Body);
			Assert.Contains("<footer class=\"gp-footer\">", page.Body);
			Assert.Contains("class=\"gp-lang\"", page.Body);
			Assert.Contains(">Terms of Service</a>", page.Body);
			Assert.Contains(">Privacy Policy</a>", page.Body);
			Assert.DoesNotContain("Rules of Engagement", page.Body);
			AssertGpClassesDefined(page.Body);
			AssertSelfContained(page.Body);
		}

		PageResponse signIn = await browser.Get("/users/sign_in");
		Assert.Equal(200, signIn.Status);
		Assert.Contains("href=\"/users/auth/discord?returnUrl=", signIn.Body);
		Assert.Contains("<h1 class=\"gp-title gp-title-centre\">Sign in to Generals Online</h1>", signIn.Body);
		Assert.Contains("class=\"gp-cover\"", signIn.Body);
		AssertPage(signIn);
		AssertPage(await browser.Get("/users/sign_up"));
		// a dead end: the reason on the icon card, and the way back (no appeal or help page exists to link)
		PageResponse unavailable = await new AccountsBrowser(probe, new Dictionary<string, string?>()).Get("/users/sign_in");
		Assert.Equal(503, unavailable.Status);
		Assert.Contains("class=\"gp-card gp-card-icon\"", unavailable.Body);
		Assert.Contains("<a href=\"/users/sign_in\" class=\"gp-btn gp-btn-outline gp-btn-fill\">Back to sign in</a>", unavailable.Body);
		Assert.DoesNotContain("Appeal", unavailable.Body);
		AssertGpClassesDefined(unavailable.Body);
		AssertSelfContained(unavailable.Body);

		Assert.Equal(302, (await PasswordPagesTests.SignUp(browser, "Styled", "styled@example.org", "a long enough password")).Status);
		AssertPage(await browser.Get("/profile/two_factor_auth"));
		PageResponse account = await browser.Get("/profile?renamed=1");
		Assert.Equal(200, account.Status);
		Assert.Contains("<nav class=\"gp-side\" aria-label=\"Your account\">", account.Body);
		Assert.Contains("class=\"gp-me\"", account.Body);
		Assert.Contains("<span class=\"gp-suffix\"", account.Body.Replace("id=\"display_name-suffix\" ", String.Empty));
		AssertGpClassesDefined(account.Body);
		AssertSelfContained(account.Body);
		await using (AppDbContext db = probe.Open())
		{
			User user = db.Users.AsTracking().Single(u => u.Email == "styled@example.org");
			user.NameRequiredAt = DateTime.UtcNow;
			await db.SaveChangesAsync();
		}
		PageResponse welcome = await browser.Get("/users/sign_up/welcome");
		Assert.Contains("class=\"gp-card gp-card-wizard\"", welcome.Body);
		Assert.Contains("<ol class=\"gp-steps\"", welcome.Body);
		AssertPage(welcome);
	}
}
