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
using Mjml.Net;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GenOnlineService.Mail
{
	// The account mails. Each is an MJML body inside Templates/layout.mjml plus a plain-text twin, embedded in the assembly
	// and compiled once by Mjml.Net (no Node.js in the build). {{name}} placeholders are filled after compiling: HTML-encoded
	// in the HTML part, as written in the text part. A placeholder without a value is an error, never a gap in a sent mail.
	// A mail goes out in the player's language (AccountsText.Supported): the subject from AccountsText, the layout, body and
	// text from that language's copies (layout.de.mjml, confirmation.de.mjml, confirmation.de.txt), each falling back to the
	// English one when a language has none.
	public static partial class EmailTemplates
	{
		public const string Confirmation = "confirmation";
		public const string PasswordReset = "password_reset";

		// the AccountsText key of each mail's subject
		private static readonly Dictionary<string, string> s_subjects = new(StringComparer.Ordinal)
		{
			[Confirmation] = "Mail.Confirmation.Subject",
			[PasswordReset] = "Mail.PasswordReset.Subject"
		};

		// by template and language
		private static readonly ConcurrentDictionary<(string Template, string Language), (string Html, string Text)> s_compiled = new();

		[GeneratedRegex(@"\{\{\s*([a-z_]+)\s*\}\}")]
		private static partial Regex Placeholder();

		// culture: the player's language; English when none is given or it is not one players can pick
		public static EmailMessage Render(string template, string to, IReadOnlyDictionary<string, string> values, CultureInfo? culture = null)
		{
			if (!s_subjects.TryGetValue(template, out string? subjectKey))
			{
				throw new ArgumentException($"No mail template named {template}.", nameof(template));
			}

			CultureInfo language = AccountsText.Resolve(culture);
			string subject = AccountsText.Get(subjectKey, language);
			(string html, string text) = s_compiled.GetOrAdd((template, language.Name), key => Compile(key.Template, key.Language));
			var withSubject = new Dictionary<string, string>(values, StringComparer.Ordinal) { ["subject"] = subject };
			return new EmailMessage(to, subject, Fill(html, withSubject, WebUtility.HtmlEncode), Fill(text, withSubject, v => v));
		}

		private static (string Html, string Text) Compile(string template, string language)
		{
			string mjml = Localized("layout", ".mjml", language).Replace("<!--body-->", Localized(template, ".mjml", language), StringComparison.Ordinal);
			(string html, ValidationErrors errors) = new MjmlRenderer().Render(mjml, new MjmlOptions { Beautify = false });
			if (errors.Count > 0)
			{
				throw new InvalidOperationException($"Mail template {template} does not compile: {String.Join("; ", errors.Select(e => e.Error))}");
			}
			return (html, Localized(template, ".txt", language));
		}

		private static string Fill(string content, IReadOnlyDictionary<string, string> values, Func<string, string> encode)
			=> Placeholder().Replace(content, match => values.TryGetValue(match.Groups[1].Value, out string? value)
				? encode(value)
				: throw new InvalidOperationException($"Mail template value {match.Groups[1].Value} is missing."));

		// A language's copy of a template file, or the English one
		private static string Localized(string file, string extension, string language)
			=> language == AccountsText.English.Name ? Resource(file + extension) : TryResource($"{file}.{language}{extension}") ?? Resource(file + extension);

		private static string Resource(string file)
			=> TryResource(file) ?? throw new InvalidOperationException($"Mail template resource GenOnlineService.Mail.Templates.{file} is missing.");

		private static string? TryResource(string file)
		{
			Assembly assembly = typeof(EmailTemplates).Assembly;
			using Stream? stream = assembly.GetManifestResourceStream($"GenOnlineService.Mail.Templates.{file}");
			if (stream == null)
			{
				return null;
			}
			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}
	}
}
