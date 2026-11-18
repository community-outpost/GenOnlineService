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
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GenOnlineService.Tests;

// Mail: one delivery_method and its own subsection; a missing or wrong value stops the start and names the key
public class MailSettingsTests
{
	private static MailSettings Read(params (string Key, string Value)[] values)
		=> MailSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>("Mail:" + v.Key, v.Value))).Build());

	private static string Refusal(params (string Key, string Value)[] values)
		=> Assert.Throws<InvalidOperationException>(() => Read(values)).Message;

	[Fact]
	public void NoMailByDefault()
	{
		MailSettings none = Read();
		Assert.Equal(DeliveryMethod.None, none.DeliveryMethod);
		Assert.False(none.Enabled);
		Assert.False(Read(("delivery_method", "none")).Enabled);
	}

	[Fact]
	public void SmtpDefaultsToStartTlsOnPort587()
	{
		MailSettings smtp = Read(("delivery_method", "smtp"), ("from", "noreply@generals.online"), ("smtp:host", "mail.example.org"), ("smtp:user", "u"), ("smtp:password", "p"));
		Assert.True(smtp.Enabled);
		Assert.Equal("noreply@generals.online", smtp.From);
		Assert.Equal(MailSettings.DefaultFromName, smtp.FromName);
		Assert.Equal("mail.example.org", smtp.Smtp.Host);
		Assert.Equal(587, smtp.Smtp.Port);
		Assert.Equal(SmtpSecurity.StartTls, smtp.Smtp.Security);
		Assert.Equal(15, smtp.TimeoutSeconds);

		MailSettings tls = Read(("delivery_method", "SMTP"), ("from", "a@b.org"), ("smtp:host", "h"), ("smtp:security", "tls"));
		Assert.Equal(465, tls.Smtp.Port);
		Assert.Null(tls.Smtp.User);
	}

	[Fact]
	public void EachApiMethodReadsItsOwnSubsection()
	{
		Assert.Equal("k1", Read(("delivery_method", "brevo"), ("from", "a@b.org"), ("brevo:api_key", "k1")).ApiKey);
		Assert.Equal("k2", Read(("delivery_method", "resend"), ("from", "a@b.org"), ("resend:api_key", "k2")).ApiKey);

		MailSettings mailgun = Read(("delivery_method", "mailgun"), ("from", "a@b.org"), ("mailgun:api_key", "k3"), ("mailgun:domain", "mg.b.org"), ("mailgun:region", "EU"));
		Assert.Equal("mg.b.org", mailgun.MailgunDomain);
		Assert.True(mailgun.MailgunEu);
		Assert.False(Read(("delivery_method", "mailgun"), ("from", "a@b.org"), ("mailgun:api_key", "k3"), ("mailgun:domain", "mg.b.org")).MailgunEu);

		MailSettings cloudflare = Read(("delivery_method", "cloudflare"), ("from", "a@b.org"), ("cloudflare:api_token", "t"), ("cloudflare:account_id", "acc"));
		Assert.Equal("t", cloudflare.ApiKey);
		Assert.Equal("acc", cloudflare.CloudflareAccountID);
	}

	[Fact]
	public void AMisconfiguredMethodNamesTheKey()
	{
		Assert.Contains("Mail:delivery_method", Refusal(("delivery_method", "sendgrid")));
		Assert.Contains("Mail:from", Refusal(("delivery_method", "resend"), ("resend:api_key", "k")));
		Assert.Contains("Mail:from", Refusal(("delivery_method", "resend"), ("from", "Name <a@b.org>"), ("resend:api_key", "k")));
		Assert.Contains("Mail:reply_to", Refusal(("delivery_method", "resend"), ("from", "a@b.org"), ("reply_to", "nope"), ("resend:api_key", "k")));
		Assert.Contains("Mail:from_name", Refusal(("delivery_method", "resend"), ("from", "a@b.org"), ("from_name", "a\r\nBcc: x@y.org"), ("resend:api_key", "k")));
		Assert.Contains("Mail:resend:api_key", Refusal(("delivery_method", "resend"), ("from", "a@b.org")));
		Assert.Contains("Mail:brevo:api_key", Refusal(("delivery_method", "brevo"), ("from", "a@b.org")));
		Assert.Contains("Mail:mailgun:domain", Refusal(("delivery_method", "mailgun"), ("from", "a@b.org"), ("mailgun:api_key", "k")));
		Assert.Contains("Mail:mailgun:region", Refusal(("delivery_method", "mailgun"), ("from", "a@b.org"), ("mailgun:api_key", "k"), ("mailgun:domain", "d"), ("mailgun:region", "asia")));
		Assert.Contains("Mail:cloudflare:account_id", Refusal(("delivery_method", "cloudflare"), ("from", "a@b.org"), ("cloudflare:api_token", "t")));
		Assert.Contains("Mail:smtp:host", Refusal(("delivery_method", "smtp"), ("from", "a@b.org")));
		Assert.Contains("Mail:smtp:security", Refusal(("delivery_method", "smtp"), ("from", "a@b.org"), ("smtp:host", "h"), ("smtp:security", "ssl3")));
		Assert.Contains("user and password", Refusal(("delivery_method", "smtp"), ("from", "a@b.org"), ("smtp:host", "h"), ("smtp:user", "u")));
		Assert.Contains("Mail:timeout_seconds", Refusal(("delivery_method", "resend"), ("from", "a@b.org"), ("resend:api_key", "k"), ("timeout_seconds", "0")));
	}

	// a refusal never repeats a secret it read
	[Fact]
	public void ARefusalNeverEchoesASecret()
	{
		string message = Refusal(("delivery_method", "mailgun"), ("from", "a@b.org"), ("mailgun:api_key", "key-SECRET123"), ("mailgun:domain", "d"), ("mailgun:region", "mars"));
		Assert.DoesNotContain("SECRET123", message);
	}
}
