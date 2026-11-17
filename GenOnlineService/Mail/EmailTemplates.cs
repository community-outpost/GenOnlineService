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

using Mjml.Net;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace GenOnlineService.Mail
{
	// The account mails. Each is an MJML body inside Templates/layout.mjml plus a plain-text twin, embedded in the assembly
	// and compiled once by Mjml.Net (no Node.js in the build). {{name}} placeholders are filled after compiling: HTML-encoded
	// in the HTML part, as written in the text part. A placeholder without a value is an error, never a gap in a sent mail.
	public static partial class EmailTemplates
	{
		public const string Confirmation = "confirmation";
		public const string PasswordReset = "password_reset";

		private static readonly Dictionary<string, string> s_subjects = new(StringComparer.Ordinal)
		{
			[Confirmation] = "Confirm your email address",
			[PasswordReset] = "Reset your Generals Online password"
		};

		private static readonly ConcurrentDictionary<string, (string Html, string Text)> s_compiled = new(StringComparer.Ordinal);

		[GeneratedRegex(@"\{\{\s*([a-z_]+)\s*\}\}")]
		private static partial Regex Placeholder();

		public static EmailMessage Render(string template, string to, IReadOnlyDictionary<string, string> values)
		{
			if (!s_subjects.TryGetValue(template, out string? subject))
			{
				throw new ArgumentException($"No mail template named {template}.", nameof(template));
			}

			(string html, string text) = s_compiled.GetOrAdd(template, Compile);
			var withSubject = new Dictionary<string, string>(values, StringComparer.Ordinal) { ["subject"] = subject };
			return new EmailMessage(to, subject, Fill(html, withSubject, WebUtility.HtmlEncode), Fill(text, withSubject, v => v));
		}

		private static (string Html, string Text) Compile(string template)
		{
			string mjml = Resource("layout.mjml").Replace("<!--body-->", Resource(template + ".mjml"), StringComparison.Ordinal);
			(string html, ValidationErrors errors) = new MjmlRenderer().Render(mjml, new MjmlOptions { Beautify = false });
			if (errors.Count > 0)
			{
				throw new InvalidOperationException($"Mail template {template} does not compile: {String.Join("; ", errors.Select(e => e.Error))}");
			}
			return (html, Resource(template + ".txt"));
		}

		private static string Fill(string content, IReadOnlyDictionary<string, string> values, Func<string, string> encode)
			=> Placeholder().Replace(content, match => values.TryGetValue(match.Groups[1].Value, out string? value)
				? encode(value)
				: throw new InvalidOperationException($"Mail template value {match.Groups[1].Value} is missing."));

		private static string Resource(string file)
		{
			Assembly assembly = typeof(EmailTemplates).Assembly;
			string name = $"GenOnlineService.Mail.Templates.{file}";
			using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Mail template resource {name} is missing.");
			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}
	}
}
