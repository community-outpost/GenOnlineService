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

using BlazorBlueprint.Icons.Lucide.Data;
using System.Globalization;
using System.Net;

namespace GenOnlineService.Identity
{
	// The language of a page and where its language switch comes back to: the same page, now in the chosen language
	public sealed record PortalLanguage(CultureInfo Culture, string ReturnUrl);

	// The look of the sign-in pages: Blazor Blueprint's "Login with Cover" blueprint (login-02), dark only, in the Swiss
	// stone palette with the logo's gold as the one accent and the box art, without its colours, as the cover
	// (wwwroot/portal/generals.css, poster.webp). The markup is
	// what BbInput, BbButton, BbLabel, BbSeparator and BbAlert render, with Lucide icons from BlazorBlueprint.Icons.Lucide's
	// data; the package only serves its prebuilt stylesheet, so no component code runs on the server. Every bb: class used
	// here is in that stylesheet (blazorblueprint.css 4.2.0): a library update can drop one, PortalLookTests catches it, and
	// a Tailwind build is the durable fix. The split layout itself is in generals.css (go-*), as the prebuilt stylesheet has
	// no lg: grid classes.
	public static class PortalLook
	{
		public const string ThemePath = "/portal/theme.css";
		public const string StylesheetPath = "/_content/BlazorBlueprint.Components/blazorblueprint.css";
		// The stone palette and gold accent over the stock dark theme, IBM Plex Sans, the split layout, the provider colours,
		// and the go-* classes
		public const string GeneralsPath = "/portal/generals.css";
		// the Zero Hour box art widened to landscape, the cover beside the form (generals.css .go-cover). Its look is baked in
		// (grey, darkened, under the veil) rather than filtered in the browser, and it is WebP: 40 KB instead of a 174 KB JPEG
		// that the browser then had to filter on every paint. Made from the 1920x1080 box art with the stylesheet's old
		// grayscale(1) brightness(0.55) contrast(1.1) and the rgb(12 10 9 / 0.35) veil:
		//   ffmpeg -i poster.jpg -vf "colorchannelmixer=.2126:.7152:.0722:0:.2126:.7152:.0722:0:.2126:.7152:.0722,
		//     lutrgb=r='E*0.65*255+12*0.35':g='E*0.65*255+10*0.35':b='E*0.65*255+9*0.35'" -c:v libwebp -quality 80
		//     -compression_level 6 poster.webp, with E = clip((((val/255*0.55)-0.5)*1.1+0.5),0,1)
		public const string PosterPath = "/portal/poster.webp";
		// Generals Online's logo, top left of every page, and GameReplays' on its button: both from the
		// playgenerals.online sign-in page
		public const string LogoPath = "/portal/logo.png";
		public const string GameReplaysLogoPath = "/portal/gamereplays.png";

		private const string InputClass = "bb:flex bb:h-10 bb:w-full bb:rounded-md bb:border bb:border-input bb:bg-transparent bb:dark:bg-input/30 bb:px-3 bb:py-2 bb:text-base bb:shadow-xs bb:placeholder:text-muted-foreground bb:focus-visible:outline-none bb:focus-visible:ring-2 bb:focus-visible:ring-ring bb:disabled:cursor-not-allowed bb:disabled:opacity-50 bb:aria-[invalid=true]:border-destructive bb:transition-colors bb:md:text-sm";
		private const string LabelClass = "bb:text-sm bb:font-medium bb:leading-none";
		private const string ButtonBase = "bb:inline-flex bb:items-center bb:justify-center bb:gap-2 bb:rounded-md bb:text-sm bb:font-medium bb:transition-colors bb:focus-visible:outline-none bb:focus-visible:ring-2 bb:focus-visible:ring-ring bb:focus-visible:ring-offset-2 bb:cursor-pointer bb:disabled:opacity-50 bb:disabled:cursor-not-allowed bb:h-10 bb:px-4 bb:py-2 bb:w-full";
		private const string PrimaryClass = ButtonBase + " bb:bg-primary bb:text-primary-foreground bb:hover:bg-primary/90";
		private const string OutlineClass = ButtonBase + " bb:border bb:border-input bb:bg-background/100 bb:shadow-xs bb:hover:bg-accent bb:hover:text-accent-foreground bb:dark:bg-input/30 bb:dark:border-input bb:dark:hover:bg-input/50";

		// The marks of the sign-in providers, which Lucide does not draw. Discord and Steam: Simple Icons (CC0), simple-icons
		// 13.21.0, in white on their brand colour. Google: its own four-colour G, as its sign-in button guidelines ask.
		private static readonly Dictionary<string, string> s_brands = new(StringComparer.Ordinal)
		{
			["discord"] = "<path fill=\"currentColor\" d=\"M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z\"/>",
			["steam"] = "<path fill=\"currentColor\" d=\"M11.979 0C5.678 0 .511 4.86.022 11.037l6.432 2.658c.545-.371 1.203-.59 1.912-.59.063 0 .125.004.188.006l2.861-4.142V8.91c0-2.495 2.028-4.524 4.524-4.524 2.494 0 4.524 2.031 4.524 4.527s-2.03 4.525-4.524 4.525h-.105l-4.076 2.911c0 .052.004.105.004.159 0 1.875-1.515 3.396-3.39 3.396-1.635 0-3.016-1.173-3.331-2.727L.436 15.27C1.862 20.307 6.486 24 11.979 24c6.627 0 11.999-5.373 11.999-12S18.605 0 11.979 0zM7.54 18.21l-1.473-.61c.262.543.714.999 1.314 1.25 1.297.539 2.793-.076 3.332-1.375.263-.63.264-1.319.005-1.949s-.75-1.121-1.377-1.383c-.624-.26-1.29-.249-1.878-.03l1.523.63c.956.4 1.409 1.5 1.009 2.455-.397.957-1.497 1.41-2.454 1.012H7.54zm11.415-9.303c0-1.662-1.353-3.015-3.015-3.015-1.665 0-3.015 1.353-3.015 3.015 0 1.665 1.35 3.015 3.015 3.015 1.663 0 3.015-1.35 3.015-3.015zm-5.273-.005c0-1.252 1.013-2.266 2.265-2.266 1.249 0 2.266 1.014 2.266 2.266 0 1.251-1.017 2.265-2.266 2.265-1.253 0-2.265-1.014-2.265-2.265z\"/>",
			["google"] = "<g transform=\"scale(0.2)\"><path fill=\"#4285F4\" d=\"M117.6,61.3636364 C117.6,57.1090909 117.218182,53.0181818 116.509091,49.0909091 L60,49.0909091 L60,72.3 L92.2909091,72.3 C90.9,79.8 86.6727273,86.1545455 80.3181818,90.4090909 L80.3181818,105.463636 L99.7090909,105.463636 C111.054545,95.0181818 117.6,79.6363636 117.6,61.3636364 L117.6,61.3636364 Z\"/><path fill=\"#34A853\" d=\"M60,120 C76.2,120 89.7818182,114.627273 99.7090909,105.463636 L80.3181818,90.4090909 C74.9454545,94.0090909 68.0727273,96.1363636 60,96.1363636 C44.3727273,96.1363636 31.1454545,85.5818182 26.4272727,71.4 L6.38181818,71.4 L6.38181818,86.9454545 C16.2545455,106.554545 36.5454545,120 60,120 L60,120 Z\"/><path fill=\"#FBBC05\" d=\"M26.4272727,71.4 C25.2272727,67.8 24.5454545,63.9545455 24.5454545,60 C24.5454545,56.0454545 25.2272727,52.2 26.4272727,48.6 L26.4272727,33.0545455 L6.38181818,33.0545455 C2.31818182,41.1545455 0,50.3181818 0,60 C0,69.6818182 2.31818182,78.8454545 6.38181818,86.9454545 L26.4272727,71.4 L26.4272727,71.4 Z\"/><path fill=\"#EA4335\" d=\"M60,23.8636364 C68.8090909,23.8636364 76.7181818,26.8909091 82.9363636,32.8363636 L100.145455,15.6272727 C89.7545455,5.94545455 76.1727273,0 60,0 C36.5454545,0 16.2545455,13.4454545 6.38181818,33.0545455 L26.4272727,48.6 C31.1454545,34.4181818 44.3727273,23.8636364 60,23.8636364 L60,23.8636364 Z\"/></g>",
		};

		private static readonly HashSet<string> s_directional = new(StringComparer.Ordinal) { "arrow-right", "log-in", "log-out" };

		private static string E(string? value) => WebUtility.HtmlEncode(value ?? String.Empty);

		// An inline Lucide icon by name; nothing for an unknown name
		public static string Icon(string name, string size = "bb:h-4 bb:w-4")
		{
			string? content = LucideIconData.GetIcon(name);
			// an arrow points the way the text reads, so it mirrors on a right-to-left page (generals.css .go-directional)
			string directional = s_directional.Contains(name) ? " go-directional" : String.Empty;
			return content == null ? String.Empty
				: $"<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" class=\"{size} bb:shrink-0{directional}\" aria-hidden=\"true\">{content}</svg>";
		}

		// A provider's mark: its own for Discord, Steam, Google and GameReplays, and a sign-in arrow for any other
		private static string ProviderMark(string provider)
		{
			if (provider == "gamereplays")
			{
				return $"<img src=\"{PortalAssets.Url(GameReplaysLogoPath)}\" alt=\"\" width=\"16\" height=\"16\" class=\"bb:h-4 bb:w-4 bb:shrink-0\">";
			}
			return s_brands.TryGetValue(provider, out string? mark)
				? $"<svg viewBox=\"0 0 24 24\" class=\"bb:h-4 bb:w-4 bb:shrink-0\" aria-hidden=\"true\">{mark}</svg>"
				: Icon("log-in");
		}

		// A whole page, as login-02: the logo top left, the title, an optional description and the already encoded body
		// centred in the left column, and the box art as the cover on the right of a wide screen
		// (in English, without the language switch, when no language is given: the staff pages' refusals)
		public static string Document(string title, string body, string? description = null, PortalLanguage? language = null)
		{
			CultureInfo culture = AccountsText.Resolve(language?.Culture);
			return Head(title, culture, cover: true)
				+ "<div class=\"go-split\">"
				+ $"<main class=\"go-main\"><a href=\"/profile\" class=\"go-logo\"><img src=\"{PortalAssets.Url(LogoPath)}\" alt=\"{E(AccountsText.Get("Brand", culture))}\" width=\"190\" height=\"69\"></a>"
				+ "<div class=\"bb:w-full bb:max-w-sm bb:space-y-6\">"
				+ $"<div class=\"bb:space-y-2 bb:text-center\"><h1 class=\"go-heading\">{E(title)}</h1>"
				+ (description == null ? String.Empty : $"<p class=\"bb:text-sm bb:text-muted-foreground\">{E(description)}</p>")
				+ "</div>"
				+ $"<div class=\"bb:space-y-4 bb:text-sm\">{body}</div>"
				+ (language == null ? String.Empty : LanguageSwitch(language))
				// the poster's address is set here, as the stylesheet cannot name a fingerprinted file
				+ $"</div></main><div class=\"go-cover\" role=\"presentation\" style=\"--go-poster: url('{PortalAssets.Url(PosterPath)}')\"></div></div></body></html>";
		}

		// The head and the opening body of every page: dark only, the theme's .dark variables with the stone palette over them
		// lang and dir follow the page's language, so Arabic reads right to left. Every file is linked by its fingerprinted
		// address (PortalAssets), cached for a year. A page with the cover preloads the poster on a screen wide enough to show
		// it, so it loads beside the stylesheets instead of after them.
		private static string Head(string title, CultureInfo culture, bool cover = false)
			=> $"<!doctype html><html lang=\"{E(culture.Name)}\" dir=\"{(AccountsText.IsRightToLeft(culture) ? "rtl" : "ltr")}\" class=\"dark\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
				+ "<meta name=\"color-scheme\" content=\"dark\"><meta name=\"theme-color\" content=\"#0c0a09\">"
				+ $"<title>{AccountsText.Format("Page.Title", culture, E(title))}</title>"
				+ $"<link rel=\"stylesheet\" href=\"{PortalAssets.Url(ThemePath)}\"><link rel=\"stylesheet\" href=\"{PortalAssets.Url(StylesheetPath)}\"><link rel=\"stylesheet\" href=\"{PortalAssets.Url(GeneralsPath)}\">"
				+ (cover ? $"<link rel=\"preload\" as=\"image\" type=\"image/webp\" href=\"{PortalAssets.Url(PosterPath)}\" media=\"(min-width: 64rem)\" fetchpriority=\"high\">" : String.Empty)
				+ "</head><body class=\"bb:bg-background bb:text-foreground\">";

		// A signed-in page of the portal (S12), after the Top Navigation Bar blueprint (nav-01) and GitLab's user settings: a
		// top bar with the logo, who is signed in and the already encoded actions on the right, then the title over the
		// settings sections
		public static string AppDocument(string title, string signedInAs, string actions, string body, PortalLanguage? language = null)
		{
			CultureInfo culture = AccountsText.Resolve(language?.Culture);
			return Head(title, culture)
				+ $"<a href=\"#content\" class=\"go-skip\">{E(AccountsText.Get("Skip", culture))}</a><header class=\"go-topbar\">"
				+ $"<a href=\"/profile\" class=\"go-topbar-logo\"><img src=\"{PortalAssets.Url(LogoPath)}\" alt=\"{E(AccountsText.Get("Brand", culture))}\" width=\"190\" height=\"69\"></a>"
				+ $"<div class=\"go-topbar-actions\">{(language == null ? String.Empty : LanguageSwitch(language))}<span class=\"bb:text-sm bb:font-medium\">{E(signedInAs)}</span>{actions}</div>"
				+ "</header>"
				+ $"<main id=\"content\" class=\"go-column\" tabindex=\"-1\"><h1 class=\"go-heading\">{E(title)}</h1>{body}</main>"
				+ "</body></html>";
		}

		// The language switch: every language in its own name, the page's own marked current, each a link that sets the
		// language cookie and comes back to the same page (AccountsController.SetLanguage)
		public static string LanguageSwitch(PortalLanguage language)
		{
			CultureInfo current = AccountsText.Resolve(language.Culture);
			string links = String.Concat(AccountsText.Supported.Select(c =>
			{
				string href = $"{LanguagePath}?culture={Uri.EscapeDataString(c.Name)}&ReturnUrl={Uri.EscapeDataString(language.ReturnUrl)}";
				string currentMark = c.Name == current.Name ? " aria-current=\"true\"" : String.Empty;
				return $"<a href=\"{E(href)}\" lang=\"{E(c.Name)}\" hreflang=\"{E(c.Name)}\" class=\"go-language\"{currentMark}>{E(AccountsText.NativeName(c))}</a>";
			}));
			return $"<nav class=\"go-languages\" aria-label=\"{E(AccountsText.Get("Language", current))}\">{links}</nav>";
		}

		public const string LanguagePath = "/users/language";

		// A settings section, as GitLab's user settings: the title and a short description in the left column, the already
		// encoded content and an optional row of buttons on the right; sections stack with a rule between them
		public static string Card(string title, string description, string content, string? footer = null)
			=> "<section class=\"go-settings\">"
				+ $"<div class=\"go-settings-intro\"><h2 class=\"go-settings-title\">{E(title)}</h2><p>{E(description)}</p></div>"
				+ $"<div class=\"go-settings-body\">{content}"
				+ (footer == null ? String.Empty : $"<div class=\"go-card-footer\">{footer}</div>")
				+ "</div></section>";

		// A small label: a role, a state
		public static string Badge(string text, bool muted = false) => $"<span class=\"go-badge{(muted ? " go-badge-muted" : String.Empty)}\">{E(text)}</span>";

		// The initials of a name in a circle, as BbAvatar's fallback
		public static string Avatar(string name)
		{
			string initials = new string(name.Where(Char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
			return $"<span class=\"go-avatar\" aria-hidden=\"true\">{E(initials.Length == 0 ? "?" : initials)}</span>";
		}

		// A row of a card: a label on the left, already encoded content on the right
		public static string Row(string label, string content)
			=> $"<div class=\"go-row\"><span class=\"bb:text-muted-foreground\">{E(label)}</span><span class=\"go-row-value\">{content}</span></div>";

		// A provider's mark and name, as on its sign-in button
		public static string ProviderName(string provider, string displayName) => $"<span class=\"go-provider-name\">{ProviderMark(provider)}<span>{E(displayName)}</span></span>";

		// Explanatory text in the muted colour
		public static string Text(string encoded) => $"<p class=\"bb:text-muted-foreground bb:leading-relaxed\">{encoded}</p>";

		// A labelled input, the label above it, with an icon inside its left edge when one is named; attributes is raw HTML
		// the caller built (type, name, autocomplete, limits)
		public static string Field(string id, string label, string attributes, string? value = null, string? icon = null)
		{
			string valueAttribute = value == null ? String.Empty : $" value=\"{E(value)}\"";
			string input = $"<input id=\"{E(id)}\" class=\"{InputClass}{(icon == null ? String.Empty : " go-iconed")}\" {attributes}{valueAttribute}>";
			if (icon != null)
			{
				input = $"<div class=\"bb:relative\"><span class=\"go-field-icon bb:text-muted-foreground\">{Icon(icon)}</span>{input}</div>";
			}
			return $"<div class=\"go-field\"><label for=\"{E(id)}\" class=\"{LabelClass}\">{E(label)}</label>{input}</div>";
		}

		// A submit button, full width in a form, its own width (narrow) in a card footer or the top bar
		public static string Submit(string text, string? name = null, string? value = null, bool outline = false, string? icon = null, bool narrow = false)
		{
			string named = name == null ? String.Empty : $" name=\"{E(name)}\" value=\"{E(value)}\"";
			return $"<button type=\"submit\"{named} class=\"{ButtonClass(outline, narrow)}\">{(icon == null ? String.Empty : Icon(icon))}{E(text)}</button>";
		}

		// A link drawn as a button, full width unless narrow
		public static string LinkButton(string href, string text, bool outline = true, string? icon = null, bool narrow = false)
			=> $"<a href=\"{E(href)}\" class=\"{ButtonClass(outline, narrow)}\">{(icon == null ? String.Empty : Icon(icon))}{E(text)}</a>";

		private static string ButtonClass(bool outline, bool narrow) => (outline ? OutlineClass : PrimaryClass) + (narrow ? " go-narrow" : String.Empty);

		// The sign-in providers, as login-02's buttons under "Or continue with": full width, one under another, each in its
		// brand's colours with its mark (an unknown provider stays an outline button)
		public static string Providers(IEnumerable<(string Href, string Provider, string DisplayName)> providers, CultureInfo? culture = null)
		{
			string buttons = String.Concat(providers.Select(p =>
				$"<a href=\"{E(p.Href)}\" class=\"{OutlineClass} go-provider go-provider-{E(p.Provider)}\">{ProviderMark(p.Provider)}{E(AccountsText.Format("SignIn.WithProvider", culture, p.DisplayName))}</a>"));
			return buttons.Length == 0 ? String.Empty : $"<div class=\"go-providers\">{buttons}</div>";
		}

		// A centred line under a form: an optional lead in the muted colour and a link, as login-02's "Don't have an account?"
		public static string Link(string href, string text, string? lead = null)
			=> $"<p class=\"bb:text-center bb:text-muted-foreground\">{(lead == null ? String.Empty : E(lead) + " ")}<a href=\"{E(href)}\" class=\"bb:text-primary bb:underline-offset-4 bb:hover:underline\">{E(text)}</a></p>";

		// A refusal or a confirmation, as BbAlert renders it, with its icon
		public static string Alert(string message, bool success = false)
		{
			string tone = success ? "bb:border-alert-success/30 bb:bg-alert-success-bg" : "bb:border-alert-danger/30 bb:bg-alert-danger-bg";
			return $"<div role=\"alert\" class=\"bb:relative bb:flex bb:gap-3 bb:w-full bb:rounded-lg bb:border bb:p-4 bb:text-foreground {tone}\">{Icon(success ? "circle-check" : "circle-alert")}<span>{E(message)}</span></div>";
		}

		// A rule between the password form and the providers, with login-02's caption on it when one is given
		public static string Separator(string? caption = null)
		{
			const string Rule = "<div role=\"none\" class=\"bb:shrink-0 bb:bg-border bb:h-[1px] bb:w-full\"></div>";
			return caption == null ? Rule : $"<div class=\"go-or\">{Rule}<span>{E(caption)}</span></div>";
		}
	}
}
