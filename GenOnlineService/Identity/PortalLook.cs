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

using System.Net;

namespace GenOnlineService.Identity
{
	// The look of the sign-in pages, from the portal trial (arctic/spike/blazor-portal, docs/portal-trial.md): Blazor
	// Blueprint's "Login with Background" blueprint (login-03), a centred card, dark only, in the stock theme with Generals
	// Online's colours over it (wwwroot/portal/generals.css). The markup is what BbCard, BbInput, BbButton, BbLabel and
	// BbAlert render; the package only serves its prebuilt stylesheet, so no component code runs on the server. Every bb:
	// class used here is in that stylesheet (blazorblueprint.css 4.2.0): a library update can drop one, PortalLookTests
	// catches it, and a Tailwind build is the durable fix.
	public static class PortalLook
	{
		public const string ThemePath = "/portal/theme.css";
		public const string StylesheetPath = "/_content/BlazorBlueprint.Components/blazorblueprint.css";
		// Generals Online's own colours over the stock dark theme: olive and khaki with an amber accent, and the go-* classes
		public const string GeneralsPath = "/portal/generals.css";

		private const string InputClass = "bb:flex bb:h-10 bb:w-full bb:rounded-md bb:border bb:border-input bb:bg-transparent bb:dark:bg-input/30 bb:px-3 bb:py-2 bb:text-base bb:shadow-xs bb:placeholder:text-muted-foreground bb:focus-visible:outline-none bb:focus-visible:ring-2 bb:focus-visible:ring-ring bb:disabled:cursor-not-allowed bb:disabled:opacity-50 bb:aria-[invalid=true]:border-destructive bb:transition-colors bb:md:text-sm";
		private const string LabelClass = "bb:text-sm bb:font-medium bb:leading-none";
		private const string ButtonBase = "bb:inline-flex bb:items-center bb:justify-center bb:gap-2 bb:rounded-md bb:text-sm bb:font-medium bb:transition-colors bb:focus-visible:outline-none bb:focus-visible:ring-2 bb:focus-visible:ring-ring bb:focus-visible:ring-offset-2 bb:cursor-pointer bb:disabled:opacity-50 bb:disabled:cursor-not-allowed bb:h-10 bb:px-4 bb:py-2 bb:w-full";
		private const string PrimaryClass = ButtonBase + " bb:bg-primary bb:text-primary-foreground bb:hover:bg-primary/90";
		private const string OutlineClass = ButtonBase + " bb:border bb:border-input bb:bg-background/100 bb:shadow-xs bb:hover:bg-accent bb:hover:text-accent-foreground bb:dark:bg-input/30 bb:dark:border-input bb:dark:hover:bg-input/50";

		// The swords mark of the trial (Lucide "swords")
		private const string Mark = "<svg width=\"24\" height=\"24\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" class=\"bb:h-6 bb:w-6\" aria-hidden=\"true\"><path d=\"M14.5 17.5L3 6V3h3l11.5 11.5M13 19l6-6m-3 3l4 4m-1 1l2-2M14.5 6.5L18 3h3v3l-3.5 3.5M5 14l4 4m-2-1l-3 3m-1-1l2 2\"/></svg>";

		private static string E(string? value) => WebUtility.HtmlEncode(value ?? String.Empty);

		// A whole page: the title in the card header and an already encoded body in its content
		public static string Document(string title, string body)
		{
			// dark only: the theme's .dark variables, with Generals Online's colours over them
			return "<!doctype html><html lang=\"en\" class=\"dark\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
				+ "<meta name=\"color-scheme\" content=\"dark\">"
				+ $"<title>{E(title)} · Generals Online</title>"
				+ $"<link rel=\"stylesheet\" href=\"{ThemePath}\"><link rel=\"stylesheet\" href=\"{StylesheetPath}\"><link rel=\"stylesheet\" href=\"{GeneralsPath}\">"
				+ "</head><body>"
				+ "<div class=\"bb:relative bb:flex bb:flex-col bb:items-center bb:justify-center bb:min-h-screen bb:bg-muted bb:p-8\">"
				+ "<div class=\"bb:absolute bb:inset-0 go-grid\"></div>"
				+ "<div class=\"bb:absolute bb:inset-0 go-vignette\"></div>"
				+ "<div class=\"bb:relative bb:z-10 bb:w-full bb:max-w-sm bb:rounded-lg bb:border bb:bg-card bb:[--bb-surface:var(--card)] bb:text-card-foreground bb:shadow-(--bb-card-shadow) go-card\">"
				+ $"<div class=\"bb:flex bb:flex-col bb:space-y-1.5 bb:p-6 bb:text-center\"><div class=\"bb:flex bb:justify-center bb:mb-2 bb:text-primary\">{Mark}</div>"
				+ $"<h1 class=\"bb:text-xl bb:font-semibold bb:leading-none go-title\">{E(title)}</h1></div>"
				+ $"<div class=\"bb:p-6 bb:pt-0 bb:space-y-4 bb:text-sm\">{body}</div></div>"
				+ "<p class=\"bb:relative bb:z-10 bb:mt-4 bb:text-center bb:text-xs bb:text-muted-foreground go-title\">Generals Online</p>"
				+ "</div></body></html>";
		}

		// Explanatory text in the muted colour
		public static string Text(string encoded) => $"<p class=\"bb:text-muted-foreground bb:leading-relaxed\">{encoded}</p>";

		// A labelled input; attributes is raw HTML the caller built (type, name, autocomplete, limits)
		public static string Field(string id, string label, string attributes, string? value = null)
		{
			string valueAttribute = value == null ? String.Empty : $" value=\"{E(value)}\"";
			return $"<div class=\"bb:space-y-2\"><label for=\"{E(id)}\" class=\"{LabelClass}\">{E(label)}</label>"
				+ $"<input id=\"{E(id)}\" class=\"{InputClass}\" {attributes}{valueAttribute}></div>";
		}

		public static string Submit(string text, string? name = null, string? value = null, bool outline = false)
		{
			string named = name == null ? String.Empty : $" name=\"{E(name)}\" value=\"{E(value)}\"";
			return $"<button type=\"submit\"{named} class=\"{(outline ? OutlineClass : PrimaryClass)}\">{E(text)}</button>";
		}

		// A link drawn as a full-width button: the providers on the sign-in page
		public static string LinkButton(string href, string text, bool outline = true)
			=> $"<a href=\"{E(href)}\" class=\"{(outline ? OutlineClass : PrimaryClass)}\">{E(text)}</a>";

		// A centred text link under a form
		public static string Link(string href, string text)
			=> $"<p class=\"bb:text-center\"><a href=\"{E(href)}\" class=\"bb:text-primary bb:underline-offset-4 bb:hover:underline\">{E(text)}</a></p>";

		// A refusal or a confirmation, as BbAlert renders it
		public static string Alert(string message, bool success = false)
		{
			string tone = success ? "bb:border-alert-success/30 bb:bg-alert-success-bg" : "bb:border-alert-danger/30 bb:bg-alert-danger-bg";
			return $"<div role=\"alert\" class=\"bb:relative bb:w-full bb:rounded-lg bb:border bb:p-4 bb:text-foreground {tone}\">{E(message)}</div>";
		}

		// "or" between the password form and the providers
		public static string Separator() => "<div role=\"none\" class=\"bb:shrink-0 bb:bg-border bb:h-[1px] bb:w-full\"></div>";
	}
}
