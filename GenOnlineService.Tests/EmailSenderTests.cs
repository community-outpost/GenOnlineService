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
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GenOnlineService.Tests;

// The four HTTP API senders: each builds its provider's request as its docs describe (checked 2026-11-17), and a refusal
// or an unreachable provider becomes EmailDeliveryException without the key
public class EmailSenderTests
{
	private const string Key = "key-SECRET-123";
	private static readonly EmailMessage s_message = new("player@example.org", "Confirm your email", "<p>Hi</p>", "Hi");

	private sealed class Recorder : HttpMessageHandler
	{
		public HttpRequestMessage? Request;
		public string Body = String.Empty;
		public HttpStatusCode Status = HttpStatusCode.OK;
		public string Answer = "{}";
		public bool Fail;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Request = request;
			Body = request.Content == null ? String.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
			if (Fail)
			{
				throw new HttpRequestException("connection refused");
			}
			return new HttpResponseMessage(Status) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") };
		}
	}

	private static MailSettings Settings(string method, params (string Key, string Value)[] own)
	{
		var values = new Dictionary<string, string?>
		{
			["Mail:delivery_method"] = method,
			["Mail:from"] = "noreply@generals.online",
			["Mail:reply_to"] = "support@generals.online"
		};
		foreach ((string key, string value) in own)
		{
			values[$"Mail:{method}:{key}"] = value;
		}
		return MailSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
	}

	private static (IEmailSender Sender, Recorder Recorder) Sender(MailSettings settings)
	{
		var recorder = new Recorder();
		return (EmailSenders.Create(settings, new HttpClient(recorder)), recorder);
	}

	[Fact]
	public async Task BrevoSendsItsTransactionalEmail()
	{
		(IEmailSender sender, Recorder recorder) = Sender(Settings("brevo", ("api_key", Key)));
		recorder.Status = HttpStatusCode.Created;
		recorder.Answer = "{\"messageId\":\"<1@relay>\"}";
		await sender.SendAsync(s_message);

		Assert.Equal(HttpMethod.Post, recorder.Request!.Method);
		Assert.Equal("https://api.brevo.com/v3/smtp/email", recorder.Request.RequestUri!.ToString());
		Assert.Equal(Key, recorder.Request.Headers.GetValues("api-key").Single());
		using JsonDocument body = JsonDocument.Parse(recorder.Body);
		Assert.Equal("noreply@generals.online", body.RootElement.GetProperty("sender").GetProperty("email").GetString());
		Assert.Equal(MailSettings.DefaultFromName, body.RootElement.GetProperty("sender").GetProperty("name").GetString());
		Assert.Equal("player@example.org", body.RootElement.GetProperty("to")[0].GetProperty("email").GetString());
		Assert.Equal("Confirm your email", body.RootElement.GetProperty("subject").GetString());
		Assert.Equal("<p>Hi</p>", body.RootElement.GetProperty("htmlContent").GetString());
		Assert.Equal("Hi", body.RootElement.GetProperty("textContent").GetString());
		Assert.Equal("support@generals.online", body.RootElement.GetProperty("replyTo").GetProperty("email").GetString());
	}

	[Fact]
	public async Task ResendSendsAnEmail()
	{
		(IEmailSender sender, Recorder recorder) = Sender(Settings("resend", ("api_key", Key)));
		recorder.Answer = "{\"id\":\"49a3999c\"}";
		await sender.SendAsync(s_message);

		Assert.Equal("https://api.resend.com/emails", recorder.Request!.RequestUri!.ToString());
		Assert.Equal("Bearer", recorder.Request.Headers.Authorization!.Scheme);
		Assert.Equal(Key, recorder.Request.Headers.Authorization.Parameter);
		using JsonDocument body = JsonDocument.Parse(recorder.Body);
		Assert.Equal("\"Generals Online\" <noreply@generals.online>", body.RootElement.GetProperty("from").GetString());
		Assert.Equal("player@example.org", body.RootElement.GetProperty("to")[0].GetString());
		Assert.Equal("<p>Hi</p>", body.RootElement.GetProperty("html").GetString());
		Assert.Equal("Hi", body.RootElement.GetProperty("text").GetString());
		Assert.Equal("support@generals.online", body.RootElement.GetProperty("reply_to").GetString());
	}

	[Fact]
	public async Task MailgunPostsAFormToItsRegion()
	{
		(IEmailSender sender, Recorder recorder) = Sender(Settings("mailgun", ("api_key", Key), ("domain", "mg.generals.online"), ("region", "eu")));
		recorder.Answer = "{\"id\":\"<1@mg>\",\"message\":\"Queued. Thank you.\"}";
		await sender.SendAsync(s_message);

		Assert.Equal("https://api.eu.mailgun.net/v3/mg.generals.online/messages", recorder.Request!.RequestUri!.ToString());
		Assert.Equal("Basic", recorder.Request.Headers.Authorization!.Scheme);
		Assert.Equal("api:" + Key, Encoding.UTF8.GetString(Convert.FromBase64String(recorder.Request.Headers.Authorization.Parameter!)));
		Assert.StartsWith("multipart/form-data", recorder.Request.Content!.Headers.ContentType!.ToString());
		foreach (string part in new[] { "name=from", "\"Generals Online\" <noreply@generals.online>", "name=to", "player@example.org", "name=subject", "name=html", "name=text", "name=\"h:Reply-To\"", "support@generals.online" })
		{
			Assert.Contains(part, recorder.Body);
		}

		(IEmailSender us, Recorder usRecorder) = Sender(Settings("mailgun", ("api_key", Key), ("domain", "mg.generals.online")));
		await us.SendAsync(s_message);
		Assert.Equal("https://api.mailgun.net/v3/mg.generals.online/messages", usRecorder.Request!.RequestUri!.ToString());
	}

	[Fact]
	public async Task CloudflareSendsThroughItsAccount()
	{
		(IEmailSender sender, Recorder recorder) = Sender(Settings("cloudflare", ("api_token", Key), ("account_id", "acc123")));
		recorder.Answer = "{\"success\":true,\"errors\":[],\"messages\":[],\"result\":{\"delivered\":[\"player@example.org\"]}}";
		await sender.SendAsync(s_message);

		Assert.Equal("https://api.cloudflare.com/client/v4/accounts/acc123/email/sending/send", recorder.Request!.RequestUri!.ToString());
		Assert.Equal(Key, recorder.Request.Headers.Authorization!.Parameter);
		using JsonDocument body = JsonDocument.Parse(recorder.Body);
		Assert.Equal("noreply@generals.online", body.RootElement.GetProperty("from").GetProperty("address").GetString());
		Assert.Equal(MailSettings.DefaultFromName, body.RootElement.GetProperty("from").GetProperty("name").GetString());
		Assert.Equal("player@example.org", body.RootElement.GetProperty("to").GetString());
		Assert.Equal("support@generals.online", body.RootElement.GetProperty("reply_to").GetString());
	}

	// a 2xx answer that says success: false is a refusal too
	[Fact]
	public async Task CloudflareSuccessFalseIsARefusal()
	{
		(IEmailSender sender, Recorder recorder) = Sender(Settings("cloudflare", ("api_token", Key), ("account_id", "acc123")));
		recorder.Answer = "{\"success\":false,\"errors\":[{\"code\":10203,\"message\":\"email.sending.disabled\"}],\"result\":null}";
		var refused = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(s_message));
		Assert.Contains("10203", refused.Message);
	}

	// the provider's status and short reason, never the key
	[Theory]
	[InlineData("brevo", "api_key")]
	[InlineData("resend", "api_key")]
	[InlineData("cloudflare", "api_token")]
	public async Task ARefusalNamesTheStatusNotTheKey(string method, string keyName)
	{
		MailSettings settings = method == "cloudflare" ? Settings(method, (keyName, Key), ("account_id", "a")) : Settings(method, (keyName, Key));
		(IEmailSender sender, Recorder recorder) = Sender(settings);
		recorder.Status = HttpStatusCode.Unauthorized;
		recorder.Answer = "{\"message\":\"Key not found: " + Key + "\",\"code\":\"unauthorized\"}";

		var refused = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(s_message));
		Assert.Contains("401", refused.Message);
		Assert.DoesNotContain(Key, refused.Message);
	}

	[Fact]
	public async Task AnUnreachableProviderIsADeliveryFailure()
	{
		(IEmailSender sender, Recorder recorder) = Sender(Settings("resend", ("api_key", Key)));
		recorder.Fail = true;
		var failed = await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(s_message));
		Assert.Contains("could not be reached", failed.Message);
	}

	// a recipient that is not one plain address (a second address, a header) is never sent
	[Theory]
	[InlineData("a@b.org, c@d.org")]
	[InlineData("a@b.org\r\nBcc: c@d.org")]
	[InlineData("not an address")]
	public async Task OnlyOnePlainRecipient(string to)
	{
		(IEmailSender sender, Recorder recorder) = Sender(Settings("resend", ("api_key", Key)));
		await Assert.ThrowsAsync<EmailDeliveryException>(() => sender.SendAsync(s_message with { To = to }));
		Assert.Null(recorder.Request);
	}

	[Fact]
	public void NoneSendsNothing()
	{
		IEmailSender none = EmailSenders.Create(MailSettings.Read(new ConfigurationBuilder().Build()), new HttpClient());
		Assert.False(none.Enabled);
	}
}
