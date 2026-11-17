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
using MimeKit;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace GenOnlineService.Tests;

// The SMTP sender through MailKit against a small in-test SMTP server: the envelope, the two parts, the sign-in, and a
// refusal or a missing server as EmailDeliveryException
public class SmtpEmailSenderTests
{
	private static readonly EmailMessage s_message = new("player@example.org", "Confirm your email", "<p>Hi</p>", "Hi");

	// Speaks just enough SMTP for one mail: EHLO (with AUTH PLAIN when a user is expected), AUTH, MAIL, RCPT, DATA, QUIT
	private sealed class FakeSmtpServer : IAsyncDisposable
	{
		private readonly TcpListener m_listener = new(IPAddress.Loopback, 0);
		private readonly Task m_serving;
		private readonly string? m_refuseRcptWith;

		public FakeSmtpServer(string? refuseRcptWith = null)
		{
			m_refuseRcptWith = refuseRcptWith;
			m_listener.Start();
			m_serving = ServeOne();
		}

		public int Port => ((IPEndPoint)m_listener.LocalEndpoint).Port;
		public string? MailFrom;
		public string? RcptTo;
		public string? AuthLine;
		public string Data = String.Empty;

		private async Task ServeOne()
		{
			using TcpClient client = await m_listener.AcceptTcpClientAsync();
			using NetworkStream stream = client.GetStream();
			using var reader = new StreamReader(stream, Encoding.ASCII);
			using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
			await writer.WriteLineAsync("220 fake ESMTP");
			while (await reader.ReadLineAsync() is string line)
			{
				string verb = line.Split(' ')[0].ToUpperInvariant();
				switch (verb)
				{
					case "EHLO":
						await writer.WriteLineAsync("250-fake");
						await writer.WriteLineAsync("250 AUTH PLAIN");
						break;
					case "AUTH":
						AuthLine = line;
						await writer.WriteLineAsync("235 2.7.0 ok");
						break;
					case "MAIL":
						MailFrom = line;
						await writer.WriteLineAsync("250 ok");
						break;
					case "RCPT":
						RcptTo = line;
						await writer.WriteLineAsync(m_refuseRcptWith ?? "250 ok");
						break;
					case "DATA":
						await writer.WriteLineAsync("354 go");
						var data = new StringBuilder();
						while (await reader.ReadLineAsync() is string body && body != ".")
						{
							data.AppendLine(body);
						}
						Data = data.ToString();
						await writer.WriteLineAsync("250 queued");
						break;
					case "RSET":
					case "NOOP":
						await writer.WriteLineAsync("250 ok");
						break;
					case "QUIT":
						await writer.WriteLineAsync("221 bye");
						return;
					default:
						await writer.WriteLineAsync("500 unknown");
						break;
				}
			}
		}

		public async ValueTask DisposeAsync()
		{
			m_listener.Stop();
			try { await m_serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
		}
	}

	private static IEmailSender Smtp(int port, params (string Key, string Value)[] extra)
	{
		var values = new Dictionary<string, string?>
		{
			["Mail:delivery_method"] = "smtp",
			["Mail:from"] = "noreply@generals.online",
			["Mail:reply_to"] = "support@generals.online",
			["Mail:timeout_seconds"] = "5",
			["Mail:smtp:host"] = "127.0.0.1",
			["Mail:smtp:port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["Mail:smtp:security"] = "none"
		};
		foreach ((string key, string value) in extra)
		{
			values["Mail:smtp:" + key] = value;
		}
		return EmailSenders.Create(MailSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build()), new HttpClient());
	}

	[Fact]
	public async Task SmtpDeliversBothPartsToOneRecipient()
	{
		await using var server = new FakeSmtpServer();
		IEmailSender sender = Smtp(server.Port, ("user", "mailer"), ("password", "s3cret"));
		Assert.True(sender.Enabled);

		await sender.SendAsync(s_message);

		Assert.Contains("<noreply@generals.online>", server.MailFrom);
		Assert.Contains("<player@example.org>", server.RcptTo);
		Assert.NotNull(server.AuthLine);
		// MimeKit quotes a display name only when it needs to; two plain words need none
		Assert.Contains("From: Generals Online <noreply@generals.online>", server.Data);
		Assert.Contains("To: player@example.org", server.Data);
		Assert.Contains("Reply-To: support@generals.online", server.Data);
		Assert.Contains("Subject: Confirm your email", server.Data);
		Assert.Contains("multipart/alternative", server.Data);
		Assert.Contains("text/plain", server.Data);
		Assert.Contains("text/html", server.Data);
	}

	[Fact]
	public async Task ARefusedRecipientIsADeliveryFailureWithoutThePassword()
	{
		await using var server = new FakeSmtpServer(refuseRcptWith: "550 5.1.1 no such user");
		IEmailSender sender = Smtp(server.Port, ("user", "mailer"), ("password", "s3cret-PASSWORD"));

		var refused = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(s_message));
		Assert.Contains("SMTP", refused.Message);
		Assert.DoesNotContain("s3cret-PASSWORD", refused.Message);
	}

	[Fact]
	public async Task NoServerIsADeliveryFailure()
	{
		// a port nothing listens on: take one and free it
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		int port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		var failed = await Assert.ThrowsAsync<EmailDeliveryException>(() => Smtp(port).SendAsync(s_message));
		Assert.Contains("could not be reached", failed.Message);
	}

	[Theory]
	[InlineData("a@b.org, c@d.org")]
	[InlineData("a@b.org\r\nBcc: c@d.org")]
	public async Task OnlyOnePlainRecipient(string to)
	{
		await using var server = new FakeSmtpServer();
		await Assert.ThrowsAsync<EmailDeliveryException>(() => Smtp(server.Port).SendAsync(s_message with { To = to }));
		Assert.Null(server.MailFrom);
	}

	// the message itself: one From with the display name, Reply-To, and text before HTML in multipart/alternative
	[Fact]
	public void TheMessageCarriesBothPartsTextFirst()
	{
		MailSettings settings = MailSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Mail:delivery_method"] = "smtp", ["Mail:from"] = "noreply@generals.online", ["Mail:smtp:host"] = "h"
		}).Build());

		MimeMessage mime = SmtpEmailSender.BuildMessage(settings, s_message);
		Assert.Equal("noreply@generals.online", mime.From.Mailboxes.Single().Address);
		Assert.Equal(MailSettings.DefaultFromName, mime.From.Mailboxes.Single().Name);
		Assert.Empty(mime.ReplyTo);
		var alternative = Assert.IsType<MultipartAlternative>(mime.Body);
		Assert.Equal("text/plain", alternative[0].ContentType.MimeType);
		Assert.Equal("text/html", alternative[1].ContentType.MimeType);
	}
}
