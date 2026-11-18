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
using System.Text;

namespace GenOnlineService.Identity
{
	// The language of a page and where its language switch comes back to: the same page, now in the chosen language
	public sealed record PortalLanguage(CultureInfo Culture, string ReturnUrl);

	// What the pages need to know about this service beyond the language: the cover images (Accounts:cover_images) and the
	// addresses of the documents the footer links: Agreements:*:url, or this service's own page of the document. A document
	// without an address is left out.
	public sealed record PortalSite(IReadOnlyList<string> CoverImages, string? TermsUrl, string? PrivacyUrl, string? RulesUrl);

	// Who is signed in, for the account shell's top bar and menu: the name and tag are shown apart, the sign-out form is the
	// controller's (it carries the antiforgery token)
	public sealed record AccountShell(string Name, string Tag, string? PlayerId, string? Role, bool Staff, string SignOutForm);

	// The look of the sign-in and account pages: the Generals Online design system (tokens.css, generals.css gp-* classes),
	// dark only, Play and IBM Plex Sans served from this service. The boards are the design's Main (sign in), ChooseName
	// (name, rules, ready), TwoFactor, CheckEmail, Refused, PhoneArabic and Account. The pages need no script: the language
	// menu and the account menu are <details>. Every file is linked by its fingerprinted address (PortalAssets). The
	// staff area (PortalDocument) links the same tokens, fonts and generals.css around Blazor Blueprint's stylesheet and
	// the design's Blueprint theme, and styles its components with its own go-* classes.
	public static class PortalLook
	{
		// The staff pages' stylesheets besides the tokens and fonts; the sign-in and account pages do not load the first two
		public const string ThemePath = "/portal/theme.css";
		public const string StylesheetPath = "/_content/BlazorBlueprint.Components/blazorblueprint.css";
		public const string GeneralsPath = "/portal/generals.css";
		// The design system's tokens (--go-*), linked first on every page, the staff pages included
		public const string TokensPath = "/portal/tokens.css";
		// Their self-hosted fonts, linked after the tokens on every page; generals.css declares no font of its own
		public const string FontsPath = "/portal/fonts.css";
		// Generals Online's radar-van logo (the project's own), top left of every page, and GameReplays' mark on its button
		public const string LogoPath = "/portal/logo.svg";
		public const string GameReplaysLogoPath = "/portal/gamereplays.png";

		// The marks of the sign-in providers, which Lucide does not draw. Discord and Steam: Simple Icons (CC0), simple-icons
		// 13.21.0, in white on their brand colour. Google: its own four-colour G, as its sign-in button guidelines ask.
		private static readonly Dictionary<string, string> s_brands = new(StringComparer.Ordinal)
		{
			["discord"] = "<path fill=\"currentColor\" d=\"M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z\"/>",
			["steam"] = "<path fill=\"currentColor\" d=\"M11.979 0C5.678 0 .511 4.86.022 11.037l6.432 2.658c.545-.371 1.203-.59 1.912-.59.063 0 .125.004.188.006l2.861-4.142V8.91c0-2.495 2.028-4.524 4.524-4.524 2.494 0 4.524 2.031 4.524 4.527s-2.03 4.525-4.524 4.525h-.105l-4.076 2.911c0 .052.004.105.004.159 0 1.875-1.515 3.396-3.39 3.396-1.635 0-3.016-1.173-3.331-2.727L.436 15.27C1.862 20.307 6.486 24 11.979 24c6.627 0 11.999-5.373 11.999-12S18.605 0 11.979 0zM7.54 18.21l-1.473-.61c.262.543.714.999 1.314 1.25 1.297.539 2.793-.076 3.332-1.375.263-.63.264-1.319.005-1.949s-.75-1.121-1.377-1.383c-.624-.26-1.29-.249-1.878-.03l1.523.63c.956.4 1.409 1.5 1.009 2.455-.397.957-1.497 1.41-2.454 1.012H7.54zm11.415-9.303c0-1.662-1.353-3.015-3.015-3.015-1.665 0-3.015 1.353-3.015 3.015 0 1.665 1.35 3.015 3.015 3.015 1.663 0 3.015-1.35 3.015-3.015zm-5.273-.005c0-1.252 1.013-2.266 2.265-2.266 1.249 0 2.266 1.014 2.266 2.266 0 1.251-1.017 2.265-2.266 2.265-1.253 0-2.265-1.014-2.265-2.265z\"/>",
			["google"] = "<g transform=\"scale(0.2)\"><path fill=\"#4285F4\" d=\"M117.6,61.3636364 C117.6,57.1090909 117.218182,53.0181818 116.509091,49.0909091 L60,49.0909091 L60,72.3 L92.2909091,72.3 C90.9,79.8 86.6727273,86.1545455 80.3181818,90.4090909 L80.3181818,105.463636 L99.7090909,105.463636 C111.054545,95.0181818 117.6,79.6363636 117.6,61.3636364 L117.6,61.3636364 Z\"/><path fill=\"#34A853\" d=\"M60,120 C76.2,120 89.7818182,114.627273 99.7090909,105.463636 L80.3181818,90.4090909 C74.9454545,94.0090909 68.0727273,96.1363636 60,96.1363636 C44.3727273,96.1363636 31.1454545,85.5818182 26.4272727,71.4 L6.38181818,71.4 L6.38181818,86.9454545 C16.2545455,106.554545 36.5454545,120 60,120 L60,120 Z\"/><path fill=\"#FBBC05\" d=\"M26.4272727,71.4 C25.2272727,67.8 24.5454545,63.9545455 24.5454545,60 C24.5454545,56.0454545 25.2272727,52.2 26.4272727,48.6 L26.4272727,33.0545455 L6.38181818,33.0545455 C2.31818182,41.1545455 0,50.3181818 0,60 C0,69.6818182 2.31818182,78.8454545 6.38181818,86.9454545 L26.4272727,71.4 L26.4272727,71.4 Z\"/><path fill=\"#EA4335\" d=\"M60,23.8636364 C68.8090909,23.8636364 76.7181818,26.8909091 82.9363636,32.8363636 L100.145455,15.6272727 C89.7545455,5.94545455 76.1727273,0 60,0 C36.5454545,0 16.2545455,13.4454545 6.38181818,33.0545455 L26.4272727,48.6 C31.1454545,34.4181818 44.3727273,23.8636364 60,23.8636364 L60,23.8636364 Z\"/></g>",
		};

		private static readonly HashSet<string> s_directional = new(StringComparer.Ordinal) { "arrow-right", "arrow-left", "log-in", "log-out" };

		private static string E(string? value) => WebUtility.HtmlEncode(value ?? String.Empty);

		// An inline Lucide icon by name (Lucide 1.64, as the design's icons); nothing for an unknown name
		public static string Icon(string name, string size = "gp-icon")
		{
			string? content = LucideIconData.GetIcon(name);
			// an arrow points the way the text reads, so it mirrors on a right-to-left page
			string directional = s_directional.Contains(name) ? " gp-directional" : String.Empty;
			return content == null ? String.Empty
				: $"<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" class=\"{size}{directional}\" aria-hidden=\"true\">{content}</svg>";
		}

		// A provider's mark: its own for Discord, Steam, Google and GameReplays, and a sign-in arrow for any other
		private static string ProviderMark(string provider)
		{
			if (provider == "gamereplays")
			{
				return $"<img src=\"{PortalAssets.Url(GameReplaysLogoPath)}\" alt=\"\" width=\"20\" height=\"20\">";
			}
			return s_brands.TryGetValue(provider, out string? mark)
				? $"<svg viewBox=\"0 0 24 24\" width=\"20\" height=\"20\" aria-hidden=\"true\">{mark}</svg>"
				: Icon("log-in");
		}

		private static string Logo(CultureInfo culture, string href = "/profile")
			=> $"<a href=\"{E(href)}\" class=\"gp-logo\"><img src=\"{PortalAssets.Url(LogoPath)}\" alt=\"{E(AccountsText.Get("Brand", culture))}\"></a>";

		// A whole page of the sign-in: the radar-van logo, the cover under its vignette, and one centred column.
		//   icon: the card that opens with an icon tile, the title and the description (two-factor, check your email, refusals)
		//   step: the account wizard's card (1 Name, 2 Rules, 3 Ready), with the title and description inside
		//   neither: the title over a card that holds the body (sign in, sign up, password reset)
		// body is already encoded. Without a language (the staff pages' refusals) there is no language menu, and without a
		// site there is no cover and no document links. wide: the wider column of the built-in documents (/legal/*).
		public static string Document(string title, string body, string? description = null, PortalLanguage? language = null, PortalSite? site = null, string? icon = null, int? step = null, string? below = null, string? heading = null, bool wide = false)
		{
			CultureInfo culture = AccountsText.Resolve(language?.Culture);
			var page = new StringBuilder(Head(title, culture));
			page.Append($"<a href=\"#content\" class=\"gp-skip\">{E(AccountsText.Get("Skip", culture))}</a><div class=\"gp-page\">");
			if (site != null && site.CoverImages.Count > 0)
			{
				string cover = site.CoverImages[site.CoverImages.Count == 1 ? 0 : Random.Shared.Next(site.CoverImages.Count)];
				page.Append($"<img class=\"gp-cover\" src=\"{E(PortalAssets.Url(cover))}\" alt=\"\" aria-hidden=\"true\" decoding=\"async\">");
			}
			page.Append("<div class=\"gp-vignette\" aria-hidden=\"true\"></div>");
			page.Append($"<main id=\"content\" class=\"gp-main\" tabindex=\"-1\">{Logo(culture)}");

			string h1 = $"<h1 class=\"gp-title{(icon == null && step == null ? " gp-title-centre" : String.Empty)}\">{E(heading ?? title)}</h1>";
			string lead = description == null ? String.Empty : $"<p class=\"gp-lead\">{E(description)}</p>";
			if (step is int current)
			{
				string head = icon == null ? $"<div class=\"gp-head\">{h1}{lead}</div>"
					: $"<div class=\"gp-banner\"><span class=\"gp-tile\">{Icon(icon, "gp-icon gp-icon-lg")}</span><div class=\"gp-head\">{h1}{lead}</div></div>";
				page.Append($"<div class=\"gp-card gp-card-wizard\">{Steps(current, culture)}{head}{body}</div>");
			}
			else if (icon != null)
			{
				page.Append($"<div class=\"gp-card gp-card-icon\"><div class=\"gp-tile\">{Icon(icon, "gp-icon gp-icon-lg")}</div><div class=\"gp-head\">{h1}{lead}</div>{body}</div>");
			}
			else
			{
				page.Append($"<div class=\"gp-stack{(wide ? " gp-stack-doc" : String.Empty)}\">{h1}{(description == null ? String.Empty : $"<p class=\"gp-lead gp-title-centre\">{E(description)}</p>")}<div class=\"gp-card\">{body}</div>{below}</div>");
			}
			page.Append(Footer(language, site, culture)).Append("</main></div></body></html>");
			return page.ToString();
		}

		// The account wizard's steps, as the design's ChooseName board: Name, Rules, Ready
		private static string Steps(int current, CultureInfo culture)
		{
			string[] names = ["Steps.Name", "Steps.Rules", "Steps.Ready"];
			var list = new StringBuilder($"<ol class=\"gp-steps\" aria-label=\"{E(AccountsText.Get("Steps.Label", culture))}\">");
			for (int index = 0; index < names.Length; index++)
			{
				string mark = index + 1 == current ? " aria-current=\"step\"" : String.Empty;
				list.Append($"<li class=\"gp-step\"{mark}><span class=\"gp-dot\">{index + 1}</span>{E(AccountsText.Get(names[index], culture))}</li>");
				if (index < names.Length - 1)
				{
					list.Append("<li class=\"gp-step-line\" aria-hidden=\"true\"></li>");
				}
			}
			return list.Append("</ol>").ToString();
		}

		// The head and the opening body of every page: dark only, the tokens first. lang and dir follow the page's language,
		// so Arabic reads right to left.
		private static string Head(string title, CultureInfo culture)
			=> $"<!doctype html><html lang=\"{E(culture.Name)}\" dir=\"{(AccountsText.IsRightToLeft(culture) ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
				+ "<meta name=\"color-scheme\" content=\"dark\"><meta name=\"theme-color\" content=\"#0B1114\">"
				+ $"<title>{AccountsText.Format("Page.Title", culture, E(title))}</title>"
				+ $"<link rel=\"stylesheet\" href=\"{PortalAssets.Url(TokensPath)}\"><link rel=\"stylesheet\" href=\"{PortalAssets.Url(FontsPath)}\"><link rel=\"stylesheet\" href=\"{PortalAssets.Url(GeneralsPath)}\">"
				+ "</head><body class=\"gp\">";

		// The footer: the language menu and, for each document with an address, its link (Help has no page yet)
		private static string Footer(PortalLanguage? language, PortalSite? site, CultureInfo culture)
		{
			var links = new StringBuilder();
			void Add(string? url, string key)
			{
				if (!String.IsNullOrWhiteSpace(url))
				{
					links.Append($"<a href=\"{E(url)}\" target=\"_blank\" rel=\"noopener noreferrer\">{E(AccountsText.Get(key, culture))}</a>");
				}
			}
			Add(site?.TermsUrl, "Agreements.TermsLink");
			Add(site?.PrivacyUrl, "Agreements.PrivacyLink");
			Add(site?.RulesUrl, "Footer.Rules");
			string nav = links.Length == 0 ? String.Empty : $"<nav aria-label=\"{E(AccountsText.Get("Footer.Label", culture))}\">{links}</nav>";
			string menu = language == null ? String.Empty : LanguageMenu(language);
			return menu.Length + nav.Length == 0 ? String.Empty : $"<footer class=\"gp-footer\">{menu}{nav}</footer>";
		}

		private static string LanguageHref(PortalLanguage language, CultureInfo target)
			=> $"{LanguagePath}?culture={Uri.EscapeDataString(target.Name)}&ReturnUrl={Uri.EscapeDataString(language.ReturnUrl)}";

		// The language menu: the current language on a button, every language in its own name below it, each a link that
		// sets the language cookie and comes back to the same page (AccountsController.SetLanguage)
		public static string LanguageMenu(PortalLanguage language)
		{
			CultureInfo current = AccountsText.Resolve(language.Culture);
			string label = $"{AccountsText.Get("Language", current)}: {AccountsText.NativeName(current)}";
			return $"<details class=\"gp-lang\"><summary aria-label=\"{E(label)}\">{Icon("globe")}{E(AccountsText.NativeName(current))}{Icon("chevron-down")}</summary>"
				+ $"<ul>{LanguageLinks(language, current, "li")}</ul></details>";
		}

		private static string LanguageLinks(PortalLanguage language, CultureInfo current, string wrap)
			=> String.Concat(AccountsText.Supported.Select(c =>
			{
				string mark = c.Name == current.Name ? " aria-current=\"true\"" : String.Empty;
				string link = $"<a href=\"{E(LanguageHref(language, c))}\" lang=\"{E(c.Name)}\" hreflang=\"{E(c.Name)}\"{mark}>{E(AccountsText.NativeName(c))}</a>";
				return wrap.Length == 0 ? link : $"<{wrap}>{link}</{wrap}>";
			}));

		public const string LanguagePath = "/users/language";

		// A signed-in page of the account (the design's Account board): the top bar with the logo and the account menu, the
		// sidebar of the area ("Your account") with the sections that exist, and the content in one centred column.
		// sections are (href, label, icon); the one whose href is current is marked. badges are a count to show at the end of a
		// section's link, by its href, with the words a screen reader says for it ("2 friend requests").
		public static string AppDocument(string title, AccountShell shell, IReadOnlyList<(string Href, string Label, string Icon)> sections, string currentHref, string body, PortalLanguage? language = null, PortalSite? site = null, IReadOnlyDictionary<string, (int Count, string Label)>? badges = null)
		{
			CultureInfo culture = AccountsText.Resolve(language?.Culture);
			string full = $"{E(shell.Name)}<span class=\"gp-tag\">{E(shell.Tag)}</span>";
			string avatar = Avatar(shell.Name);
			string who = shell.PlayerId == null && shell.Role == null ? String.Empty
				: $"<span class=\"gp-hint\">{(shell.PlayerId == null ? String.Empty : E(AccountsText.Format("Account.PlayerId", culture, shell.PlayerId)))}{(shell.PlayerId != null && shell.Role != null ? " · " : String.Empty)}{E(shell.Role)}</span>";

			var menu = new StringBuilder($"<div class=\"gp-menu-head\">{avatar}<div class=\"gp-head\" style=\"gap:2px;min-width:0\"><bdi class=\"gp-name\">{full}</bdi>{who}</div></div><hr>");
			menu.Append($"<a href=\"/profile\">{Icon("user")}{E(AccountsText.Get("Account.EditProfile", culture))}</a>");
			if (language != null)
			{
				CultureInfo current = AccountsText.Resolve(language.Culture);
				menu.Append($"<span class=\"gp-hint\" style=\"display:flex;align-items:center;gap:10px;padding:4px 10px 0\">{Icon("globe")}{E(AccountsText.Get("Language", culture))}</span><div class=\"gp-menu-langs\">{LanguageLinks(language, current, String.Empty)}</div>");
			}
			if (shell.Staff)
			{
				menu.Append($"<hr><a href=\"/admin\">{Icon("layout-dashboard")}{E(AccountsText.Get("Account.StaffTools", culture))}</a>");
			}
			menu.Append($"<hr>{shell.SignOutForm}");

			var side = new StringBuilder($"<nav class=\"gp-side\" aria-label=\"{E(AccountsText.Get("Account.Area", culture))}\"><span class=\"gp-area\">{E(AccountsText.Get("Account.Area", culture))}</span>");
			foreach ((string href, string label, string icon) in sections)
			{
				string count = badges != null && badges.TryGetValue(href, out (int Count, string Label) badge) && badge.Count > 0
					? $"<span class=\"gp-count\" aria-label=\"{E(badge.Label)}\">{badge.Count.ToString(CultureInfo.InvariantCulture)}</span>" : String.Empty;
				side.Append($"<a href=\"{E(href)}\"{(href == currentHref ? " aria-current=\"page\"" : String.Empty)}>{Icon(icon)}{E(label)}{count}</a>");
			}
			side.Append("</nav>");

			string crumb = sections.FirstOrDefault(s => s.Href == currentHref).Label ?? title;
			return Head(title, culture)
				+ $"<a href=\"#content\" class=\"gp-skip\">{E(AccountsText.Get("Skip", culture))}</a><div class=\"gp-shell\"><header class=\"gp-top\">{Logo(culture)}<span class=\"gp-top-space\"></span>"
				+ $"<details class=\"gp-me\"><summary aria-label=\"{E(AccountsText.Format("Account.MenuLabel", culture, shell.Name + shell.Tag))}\">{avatar}<bdi class=\"gp-name\">{full}</bdi>{Icon("chevron-down")}</summary><div class=\"gp-menu\">{menu}</div></details></header>"
				+ $"<div class=\"gp-body\">{side}<main id=\"content\" class=\"gp-content\" tabindex=\"-1\"><div class=\"gp-column\">"
				+ $"<p class=\"gp-crumbs\">{E(AccountsText.Get("Account.Area", culture))} / {E(crumb)}</p><h1 class=\"gp-title\">{E(title)}</h1>{body}</div></main></div>"
				+ Footer(null, site, culture)
				+ "</div></body></html>";
		}

		// A settings section of the account page: the title, a short description and the already encoded content, with an
		// optional row of buttons; sections stack with a rule between them
		public static string Card(string title, string description, string content, string? footer = null)
			=> $"<section class=\"gp-section\"><h2>{E(title)}</h2><p class=\"gp-section-desc\">{E(description)}</p>{content}"
				+ (footer == null ? String.Empty : $"<div class=\"gp-actions\">{footer}</div>")
				+ "</section>";

		// The line under a page's title (the boards' short description of the page)
		public static string Intro(string text) => $"<p class=\"gp-intro\">{E(text)}</p>";

		// A player's initials in a tile: the first two letters or digits of the name
		public static string Avatar(string name)
		{
			string initials = new string(name.Where(Char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
			return $"<span class=\"gp-avatar\" aria-hidden=\"true\">{E(initials.Length == 0 ? "?" : initials)}</span>";
		}

		// A list of players (friends, requests, blocked players): each row is an item of Person
		public static string People(IEnumerable<string> rows) => $"<ul class=\"gp-people\">{String.Concat(rows)}</ul>";

		// One player of a list: the avatar, the name with its tag (the name is stored text, so it is encoded here), a line under
		// it (already encoded) and the row's controls (already encoded)
		public static string Person(string name, string tag, string meta, string actions)
			=> $"<li class=\"gp-person\">{Avatar(name)}<div class=\"gp-who\"><bdi class=\"gp-name\">{E(name)}<span class=\"gp-tag\">{E(tag)}</span></bdi><span class=\"gp-meta\">{meta}</span></div>{actions}</li>";

		// A friend's presence in the line under their name: a dot and the words, green while they are online
		public static string Presence(string text, bool online)
			=> $"<span class=\"gp-presence{(online ? " gp-presence-online" : String.Empty)}\"><span class=\"gp-pip\" aria-hidden=\"true\"></span>{E(text)}</span>";

		// What a region shows when it has nothing to show (the design's EmptyState): an icon, a short title, and a line on what
		// to do. The title is a heading one level under the section's.
		public static string Empty(string icon, string title, string? description = null, bool small = false)
			=> $"<div class=\"gp-empty{(small ? " gp-empty-sm" : String.Empty)}\">{(small ? Icon(icon) : $"<span class=\"gp-empty-well\">{Icon(icon, "gp-icon gp-icon-lg")}</span>")}"
				+ $"<h3 class=\"gp-empty-title\">{E(title)}</h3>{(description == null ? String.Empty : $"<p class=\"gp-empty-text\">{E(description)}</p>")}</div>";

		// A button for a form that sits in a list row or beside a field: its own width, the 44px of the default size. style is
		// primary, secondary, outline or ghost (the design's Button variants).
		public static string ActionButton(string text, string style = "primary", string? icon = null)
			=> $"<button type=\"submit\" class=\"gp-btn gp-btn-{E(style)}\">{(icon == null ? String.Empty : Icon(icon))}{E(text)}</button>";

		// A menu of buttons behind one (the "more" of a list row); items are forms made with MenuItem. A details element, so it
		// needs no script.
		public static string MoreMenu(string label, string items)
			=> $"<details class=\"gp-more\"><summary aria-label=\"{E(label)}\">{Icon("ellipsis")}</summary><div class=\"gp-more-menu\">{items}</div></details>";

		public static string MenuItem(string text, string? icon = null) => $"<button type=\"submit\">{(icon == null ? String.Empty : Icon(icon))}{E(text)}</button>";

		// The add-a-friend and block-a-player form (the boards' Friends and Blocked): a labelled field for a name and tag or an
		// id, the button beside it and the hint under both. form is the opening tag with its antiforgery token (the
		// controller's), the placeholder and the hint are plain text.
		public static string PlayerForm(string form, string id, string name, string label, string placeholder, string hint, string button)
			=> $"{form}<div class=\"gp-field\"><label for=\"{E(id)}\" class=\"gp-label\">{E(label)}</label>"
				+ $"<input id=\"{E(id)}\" class=\"gp-input\" type=\"text\" name=\"{E(name)}\" placeholder=\"{E(placeholder)}\" maxlength=\"64\" autocomplete=\"off\" autocapitalize=\"off\" spellcheck=\"false\" aria-describedby=\"{E(id)}-hint\" required></div>"
				+ $"{button}<p id=\"{E(id)}-hint\" class=\"gp-hint\">{E(hint)}</p></form>";

		// A small label: a role, a state
		public static string Badge(string text, bool muted = false) => $"<span class=\"gp-badge{(muted ? " gp-badge-muted" : String.Empty)}\">{E(text)}</span>";

		// A row of a section: a label at the start, already encoded content at the end
		public static string Row(string label, string content)
			=> $"<div class=\"gp-row\"><span class=\"gp-row-label\">{E(label)}</span><span class=\"gp-row-value\">{content}</span></div>";

		// A provider's mark and name, as on its sign-in button
		public static string ProviderName(string provider, string displayName) => $"<span class=\"gp-provider\">{ProviderMark(provider)}<span>{E(displayName)}</span></span>";

		// Explanatory text
		public static string Text(string encoded) => $"<p class=\"gp-copy\">{encoded}</p>";

		// A muted line, such as a hint under a form
		public static string Muted(string encoded) => $"<p class=\"gp-muted\">{encoded}</p>";

		// A labelled input, the label above it. attributes is raw HTML the caller built (type, name, autocomplete, limits).
		//   suffix: a fixed end of the value shown inside the control (the player's #tag), isolated left to right and named
		//   in aria-describedby with the hint
		//   hint: a short line below
		//   aside: already encoded content opposite the label ("Forgot your password?")
		public static string Field(string id, string label, string attributes, string? value = null, string? suffix = null, string? hint = null, string? aside = null, bool large = false, bool code = false, string? suffixLabel = null, string? error = null)
		{
			string valueAttribute = value == null ? String.Empty : $" value=\"{E(value)}\"";
			var described = new List<string>();
			if (suffix != null)
			{
				described.Add(id + "-suffix");
			}
			if (hint != null || error != null)
			{
				described.Add(id + "-hint");
			}
			string describedBy = described.Count == 0 ? String.Empty : $" aria-describedby=\"{String.Join(' ', described)}\"";
			string input = $"<input id=\"{E(id)}\" class=\"gp-input{(large ? " gp-input-lg" : String.Empty)}{(code ? " gp-code" : String.Empty)}\"{describedBy} {attributes}{valueAttribute}>";
			if (suffix != null)
			{
				input = $"<div class=\"gp-input-wrap\">{input}<span id=\"{E(id)}-suffix\" class=\"gp-suffix\" dir=\"ltr\"{(suffixLabel == null ? String.Empty : $" aria-label=\"{E(suffixLabel)}\"")}>{E(suffix)}</span></div>";
			}
			string labelHtml = aside == null ? $"<label for=\"{E(id)}\" class=\"gp-label\">{E(label)}</label>"
				: $"<div class=\"gp-label-row\"><label for=\"{E(id)}\" class=\"gp-label\">{E(label)}</label>{aside}</div>";
			return $"<div class=\"gp-field\">{labelHtml}{input}{(error != null ? $"<p id=\"{E(id)}-hint\" class=\"gp-hint gp-error\" role=\"alert\">{Icon("circle-alert")}{E(error)}</p>" : hint == null ? String.Empty : $"<p id=\"{E(id)}-hint\" class=\"gp-hint\">{hint}</p>")}</div>";
		}

		// A submit button, full width in a form, its own width (narrow) in a section's buttons
		public static string Submit(string text, string? name = null, string? value = null, bool outline = false, string? icon = null, bool narrow = false, bool large = false, bool fill = false)
		{
			string named = name == null ? String.Empty : $" name=\"{E(name)}\" value=\"{E(value)}\"";
			return $"<button type=\"submit\"{named} class=\"{ButtonClass(outline, narrow, large, fill)}\">{(icon == null ? String.Empty : Icon(icon))}{E(text)}</button>";
		}

		// A link drawn as a button, full width unless narrow
		public static string LinkButton(string href, string text, bool outline = true, string? icon = null, bool narrow = false, bool large = false, bool fill = false)
			=> $"<a href=\"{E(href)}\" class=\"{ButtonClass(outline, narrow, large, fill)}\">{(icon == null ? String.Empty : Icon(icon))}{E(text)}</a>";

		private static string ButtonClass(bool outline, bool narrow, bool large, bool fill)
			=> "gp-btn " + (outline ? "gp-btn-outline" : "gp-btn-primary") + (large ? " gp-btn-lg" : narrow ? " gp-btn-sm" : String.Empty) + (fill ? " gp-btn-fill" : narrow ? String.Empty : " gp-btn-block");

		// The pinned row of a card with steps: a quiet button for the way back and the one primary action beside it
		public static string Actions(string? back, string primary) => $"<div class=\"gp-actions gp-actions-pinned\">{back}{primary}</div>";

		// The row that ends a dead-end card: the way back to sign in (or to the account) as an outline button
		public static string Way(string href, string text) => $"<div class=\"gp-actions\"><a href=\"{E(href)}\" class=\"gp-btn gp-btn-outline gp-btn-fill\">{E(text)}</a></div>";

		// The way on under a card's form: an optional muted line, then the arrow back
		public static string WayOn(string href, string text, string? extra = null) => $"<div class=\"gp-way-on\">{extra}{BackLink(href, text)}</div>";

		public static string GhostLink(string href, string text, string? icon = null)
			=> $"<a href=\"{E(href)}\" class=\"gp-btn gp-btn-ghost\">{(icon == null ? String.Empty : Icon(icon))}{E(text)}</a>";

		public static string Disabled(string text) => $"<button type=\"button\" class=\"gp-btn gp-btn-primary\" disabled>{E(text)}</button>";

		// The sign-in providers: an equal grid of buttons in the one frame, each in its brand's colours with its mark (an
		// unknown provider stays an outline button)
		public static string Providers(IEnumerable<(string Href, string Provider, string DisplayName)> providers, CultureInfo? culture = null)
		{
			// the design's order: Discord, Steam, Google, GameReplays, then any other as configured
			string[] order = ["discord", "steam", "google", "gamereplays"];
			string buttons = String.Concat(providers.OrderBy(p => Array.IndexOf(order, p.Provider) is int at and >= 0 ? at : order.Length).Select(p =>
				$"<a href=\"{E(p.Href)}\" class=\"gp-prov gp-prov-{(s_brands.ContainsKey(p.Provider) || p.Provider == "gamereplays" ? E(p.Provider) : "other")}\">{ProviderMark(p.Provider)}<span>{E(AccountsText.Format("SignIn.WithProvider", culture, p.DisplayName))}</span></a>"));
			return buttons.Length == 0 ? String.Empty : $"<div class=\"gp-providers\">{buttons}</div>";
		}

		// A centred line under a form: an optional lead and a link, as "Don't have an account yet? Register now"
		public static string Link(string href, string text, string? lead = null)
			=> $"<p class=\"gp-centre\">{(lead == null ? String.Empty : E(lead) + " ")}<a href=\"{E(href)}\">{E(text)}</a></p>";

		// The way back on a card: an arrow and the words
		public static string BackLink(string href, string text) => $"<a href=\"{E(href)}\" class=\"gp-back\">{Icon("arrow-left")}{E(text)}</a>";

		// A refusal or a confirmation, with its icon
		public static string Alert(string message, bool success = false)
			=> $"<div role=\"{(success ? "status" : "alert")}\" class=\"gp-alert{(success ? " gp-alert-success" : String.Empty)}\">{Icon(success ? "circle-check" : "circle-alert", "gp-icon")}<span>{E(message)}</span></div>";

		// A rule between the password form and the providers, with a caption on it when one is given
		public static string Separator(string? caption = null) => $"<div class=\"gp-or\"{(caption == null ? " role=\"separator\"" : String.Empty)}>{(caption == null ? String.Empty : $"<span>{E(caption)}</span>")}</div>";
	}
}
