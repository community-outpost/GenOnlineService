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

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace GenOnlineService.Identity
{
	// The built-in Rules of Engagement, Terms of Service and Privacy Policy: English HTML fragments embedded from
	// Identity/Legal/<name>.html, served by AccountsController at /legal/<name> to anyone, and linked wherever a document has
	// no Agreements:<doc>:url of its own. The rules have a section per rule card, under the card's id (#respect...).
	// HTML comments in a fragment are notes for the operators (open facts) and are never served.
	public static partial class LegalPages
	{
		public const string Rules = "rules";
		public const string Terms = "terms";
		public const string Privacy = "privacy";

		public const string RulesPath = "/legal/" + Rules;
		public const string TermsPath = "/legal/" + Terms;
		public const string PrivacyPath = "/legal/" + Privacy;

		private static readonly ConcurrentDictionary<string, string> s_fragments = new(StringComparer.Ordinal);

		[GeneratedRegex("<!--.*?-->\\s*", RegexOptions.Singleline)]
		private static partial Regex Comment();

		// One document's fragment, without its comments
		public static string Fragment(string name) => s_fragments.GetOrAdd(name, static n =>
		{
			using Stream stream = typeof(LegalPages).Assembly.GetManifestResourceStream($"GenOnlineService.Identity.Legal.{n}.html")
				?? throw new InvalidOperationException($"The built-in page {n} is not embedded.");
			using var reader = new StreamReader(stream);
			return Comment().Replace(reader.ReadToEnd(), String.Empty).Trim();
		});

		// The page's body: the version in force and the day it took effect when configured, then the document. The text is
		// English and left to right whatever the page's language.
		public static string Body(string name, AgreementDocument? document)
		{
			string meta = Meta(document);
			return $"<div class=\"gp-doc\" lang=\"en\" dir=\"ltr\">{(meta.Length == 0 ? String.Empty : $"<p class=\"gp-doc-meta\">{WebUtility.HtmlEncode(meta)}</p>")}{Fragment(name)}</div>";
		}

		private static string Meta(AgreementDocument? document)
		{
			string? day = document?.Date?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
			return (document?.Version, day) switch
			{
				(string version, string date) => $"Version {version}, in effect from {date}",
				(string version, null) => $"Version {version}",
				(null, string date) => $"In effect from {date}",
				_ => String.Empty
			};
		}
	}
}
