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

using GenOnlineService.Mail;
using Xunit;

namespace GenOnlineService.Tests;

// The account mails: MJML compiled without errors, placeholders filled (HTML-encoded in the HTML part, as written in the
// text part), every mail with a plain-text part, and an unfilled placeholder an error rather than a gap in a sent mail
public class EmailTemplatesTests
{
	private static readonly Dictionary<string, string> s_values = new()
	{
		["name"] = "Nelly#0042",
		["link"] = "https://api-test.generals.online/users/confirmation?confirmation_token=abc&x=1"
	};

	[Theory]
	[InlineData(EmailTemplates.Confirmation)]
	[InlineData(EmailTemplates.PasswordReset)]
	public void EveryTemplateCompilesAndFills(string template)
	{
		EmailMessage mail = EmailTemplates.Render(template, "player@example.org", s_values);

		Assert.Equal("player@example.org", mail.To);
		Assert.False(String.IsNullOrWhiteSpace(mail.Subject));
		Assert.StartsWith("<!doctype html>", mail.Html.TrimStart(), StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Nelly#0042", mail.Html);
		Assert.Contains("Nelly#0042", mail.Text);
		// the link is a real href in HTML (its & encoded) and plain in the text part
		Assert.Contains("confirmation_token=abc&amp;x=1", mail.Html);
		Assert.Contains("confirmation_token=abc&x=1", mail.Text);
		Assert.DoesNotContain("{{", mail.Html);
		Assert.DoesNotContain("{{", mail.Text);
	}

	// a value is text, never markup
	[Fact]
	public void AValueCannotInjectMarkup()
	{
		var values = new Dictionary<string, string>(s_values) { ["name"] = "<script>alert(1)</script>\"" };
		EmailMessage mail = EmailTemplates.Render(EmailTemplates.Confirmation, "player@example.org", values);

		Assert.DoesNotContain("<script>", mail.Html);
		Assert.Contains("&lt;script&gt;", mail.Html);
	}

	[Fact]
	public void AMissingValueIsAnError()
	{
		var refused = Assert.Throws<InvalidOperationException>(() => EmailTemplates.Render(EmailTemplates.Confirmation, "player@example.org", new Dictionary<string, string> { ["name"] = "x" }));
		Assert.Contains("link", refused.Message);
	}

	[Fact]
	public void AnUnknownTemplateIsAnError()
	{
		Assert.Throws<ArgumentException>(() => EmailTemplates.Render("nope", "player@example.org", s_values));
	}
}
