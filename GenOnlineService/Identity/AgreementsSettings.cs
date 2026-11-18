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

using System.Globalization;

namespace GenOnlineService.Identity
{
	// One document a player agrees to (S19): the version in force, the day it took effect and where to read it.
	// Without a version the document is not required, and nobody is asked to agree to it.
	public sealed class AgreementDocument
	{
		public const int VersionMaxLength = 32;

		public string Name { get; init; } = String.Empty;
		public string? Version { get; init; }
		public DateOnly? Date { get; init; }
		public string? Url { get; init; }

		public bool Required => Version != null;
	}

	// Agreements: the Rules of Engagement ("rules") and the Terms of Service ("terms") each account agrees to, and the
	// Privacy Policy, which is only linked. Neither version configured means nothing is gated at all (self-hosters, tests).
	// A wording-only fix keeps the version; a new version asks every player again at their next sign-in.
	public sealed class AgreementsSettings
	{
		public const string Rules = "rules";
		public const string Terms = "terms";

		// In the order the sign-in asks: the rule cards, then the Terms
		public static readonly IReadOnlyList<string> DocumentNames = [Rules, Terms];

		public AgreementDocument RulesDocument { get; init; } = new() { Name = Rules };
		public AgreementDocument TermsDocument { get; init; } = new() { Name = Terms };
		public string? PrivacyUrl { get; init; }

		public IEnumerable<AgreementDocument> Documents => [RulesDocument, TermsDocument];

		// The documents with a version, the ones a player must agree to
		public IEnumerable<AgreementDocument> Required => Documents.Where(d => d.Required);

		public bool Any => Required.Any();

		public AgreementDocument? Find(string? name) => Documents.FirstOrDefault(d => d.Name == name);

		public static AgreementsSettings Read(IConfigurationSection section)
		{
			string? privacy = section.GetSection("privacy").GetValue<string>("url");
			return new AgreementsSettings
			{
				RulesDocument = ReadDocument(section, Rules),
				TermsDocument = ReadDocument(section, Terms),
				PrivacyUrl = String.IsNullOrWhiteSpace(privacy) ? null : privacy.Trim()
			};
		}

		private static AgreementDocument ReadDocument(IConfigurationSection section, string name)
		{
			IConfigurationSection document = section.GetSection(name);
			string? version = Blank(document.GetValue<string>("version"));
			string? url = Blank(document.GetValue<string>("url"));
			string? dateText = Blank(document.GetValue<string>("date"));

			DateOnly? date = null;
			if (dateText != null)
			{
				if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
				{
					throw new InvalidOperationException($"Agreements:{name}:date must be a day as yyyy-MM-dd, such as 2026-11-18.");
				}
				date = parsed;
			}
			if (version != null)
			{
				if (version.Length > AgreementDocument.VersionMaxLength)
				{
					throw new InvalidOperationException($"Agreements:{name}:version must be at most {AgreementDocument.VersionMaxLength} characters.");
				}
				if (url == null)
				{
					throw new InvalidOperationException($"Agreements:{name}:url is required when Agreements:{name}:version is set: players must be able to read what they agree to.");
				}
			}

			return new AgreementDocument { Name = name, Version = version, Date = date, Url = url };
		}

		private static string? Blank(string? value) => String.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}
}
