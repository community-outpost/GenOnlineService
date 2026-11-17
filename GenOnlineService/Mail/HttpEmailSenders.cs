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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenOnlineService.Mail
{
	public static class EmailSenders
	{
		// The sender for Mail:delivery_method; http carries the API calls (a test passes one with its own handler)
		public static IEmailSender Create(MailSettings settings, HttpClient http) => settings.DeliveryMethod switch
		{
			DeliveryMethod.None => new DisabledEmailSender(),
			DeliveryMethod.Brevo => new BrevoEmailSender(settings, http),
			DeliveryMethod.Resend => new ResendEmailSender(settings, http),
			DeliveryMethod.Mailgun => new MailgunEmailSender(settings, http),
			DeliveryMethod.Cloudflare => new CloudflareEmailSender(settings, http),
			DeliveryMethod.Smtp => new SmtpEmailSender(settings),
			_ => throw new NotSupportedException($"Mail:delivery_method {settings.DeliveryMethod} has no sender.")
		};
	}

	// A provider's HTTP API: one request per mail, a 2xx answer is accepted, anything else is a refusal. Messages name the
	// provider, the status and its short error, never the key, which only travels in a header.
	public abstract class HttpEmailSender : IEmailSender
	{
		private const int MaximumErrorLength = 200;

		protected HttpEmailSender(MailSettings settings, HttpClient http)
		{
			Settings = settings;
			Http = http;
		}

		protected MailSettings Settings { get; }
		protected HttpClient Http { get; }
		protected abstract string Provider { get; }

		public bool Enabled => true;

		// "Generals Online" <noreply@…>: from_name holds no quote, angle bracket or line break (MailSettings checks)
		protected string FromHeader => $"\"{Settings.FromName}\" <{Settings.From}>";

		public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
		{
			if (!MailSettings.IsAddress(message.To))
			{
				throw new EmailDeliveryException($"{Provider}: the recipient is not one email address.");
			}

			using HttpRequestMessage request = Build(message);
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(Settings.TimeoutSeconds));
			HttpResponseMessage response;
			try
			{
				response = await Http.SendAsync(request, timeout.Token);
			}
			catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
			{
				throw new EmailDeliveryException($"{Provider} could not be reached ({ex.GetType().Name}).", ex);
			}

			using (response)
			{
				string body = await response.Content.ReadAsStringAsync(cancellationToken);
				string? refusal = response.IsSuccessStatusCode ? RefusalIn(body) : $"HTTP {(int)response.StatusCode} {ErrorIn(body)}".TrimEnd();
				if (refusal != null)
				{
					throw new EmailDeliveryException($"{Provider} refused the mail: {Scrub(refusal)}");
				}
			}
		}

		protected abstract HttpRequestMessage Build(EmailMessage message);

		// A 2xx answer that still says the mail was refused (Cloudflare's success: false)
		protected virtual string? RefusalIn(string body) => null;

		// The provider's own short error: its code and message when the answer is JSON
		private static string ErrorIn(string body)
		{
			try
			{
				JsonNode? node = JsonNode.Parse(body);
				JsonNode? error = node?["errors"] is JsonArray { Count: > 0 } errors ? errors[0] : node;
				string code = error?["code"]?.ToString() ?? error?["name"]?.ToString() ?? String.Empty;
				string text = error?["message"]?.ToString() ?? String.Empty;
				return $"{code} {text}".Trim();
			}
			catch (JsonException)
			{
				return String.Empty;
			}
		}

		// Never repeat the key, even when a provider echoes it, and keep the line short
		private string Scrub(string text)
		{
			if (!String.IsNullOrEmpty(Settings.ApiKey))
			{
				text = text.Replace(Settings.ApiKey, "[key]", StringComparison.Ordinal);
			}
			return text.Length > MaximumErrorLength ? text.Substring(0, MaximumErrorLength) : text;
		}

		protected static HttpContent Json(object body) => JsonContent.Create(body, options: new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
	}

	// Brevo transactional email: POST /v3/smtp/email, the key in the api-key header, 201 {messageId}
	public sealed class BrevoEmailSender : HttpEmailSender
	{
		public BrevoEmailSender(MailSettings settings, HttpClient http) : base(settings, http) { }

		protected override string Provider => "Brevo";

		protected override HttpRequestMessage Build(EmailMessage message)
		{
			var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
			{
				Content = Json(new
				{
					sender = new { name = Settings.FromName, email = Settings.From },
					to = new[] { new { email = message.To } },
					subject = message.Subject,
					htmlContent = message.Html,
					textContent = message.Text,
					replyTo = Settings.ReplyTo == null ? null : new { email = Settings.ReplyTo }
				})
			};
			request.Headers.Add("api-key", Settings.ApiKey);
			return request;
		}
	}

	// Resend: POST /emails with a bearer key, from as "Name <address>"
	public sealed class ResendEmailSender : HttpEmailSender
	{
		public ResendEmailSender(MailSettings settings, HttpClient http) : base(settings, http) { }

		protected override string Provider => "Resend";

		protected override HttpRequestMessage Build(EmailMessage message)
		{
			var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
			{
				Content = Json(new
				{
					from = FromHeader,
					to = new[] { message.To },
					subject = message.Subject,
					html = message.Html,
					text = message.Text,
					reply_to = Settings.ReplyTo
				})
			};
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.ApiKey);
			return request;
		}
	}

	// Mailgun: a multipart form to /v3/<domain>/messages in the domain's region, basic auth as api:<key>
	public sealed class MailgunEmailSender : HttpEmailSender
	{
		public MailgunEmailSender(MailSettings settings, HttpClient http) : base(settings, http) { }

		protected override string Provider => "Mailgun";

		protected override HttpRequestMessage Build(EmailMessage message)
		{
			string baseUrl = Settings.MailgunEu ? "https://api.eu.mailgun.net" : "https://api.mailgun.net";
			var form = new MultipartFormDataContent
			{
				{ new StringContent(FromHeader), "from" },
				{ new StringContent(message.To), "to" },
				{ new StringContent(message.Subject), "subject" },
				{ new StringContent(message.Html), "html" },
				{ new StringContent(message.Text), "text" }
			};
			if (Settings.ReplyTo != null)
			{
				form.Add(new StringContent(Settings.ReplyTo), "h:Reply-To");
			}

			var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/{Uri.EscapeDataString(Settings.MailgunDomain!)}/messages") { Content = form };
			request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("api:" + Settings.ApiKey)));
			return request;
		}
	}

	// Cloudflare Email Service (beta): POST /accounts/<account>/email/sending/send with an API token; the answer is
	// Cloudflare's envelope, whose success: false is a refusal even on a 2xx status
	public sealed class CloudflareEmailSender : HttpEmailSender
	{
		public CloudflareEmailSender(MailSettings settings, HttpClient http) : base(settings, http) { }

		protected override string Provider => "Cloudflare";

		protected override HttpRequestMessage Build(EmailMessage message)
		{
			var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(Settings.CloudflareAccountID!)}/email/sending/send")
			{
				Content = Json(new
				{
					from = new { address = Settings.From, name = Settings.FromName },
					to = message.To,
					subject = message.Subject,
					html = message.Html,
					text = message.Text,
					reply_to = Settings.ReplyTo
				})
			};
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.ApiKey);
			return request;
		}

		protected override string? RefusalIn(string body)
		{
			try
			{
				JsonNode? node = JsonNode.Parse(body);
				if (node?["success"]?.GetValue<bool>() == false)
				{
					JsonNode? error = node["errors"] is JsonArray { Count: > 0 } errors ? errors[0] : null;
					return $"{error?["code"]} {error?["message"]}".Trim();
				}
				return null;
			}
			catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is FormatException)
			{
				// an answer that is not Cloudflare's envelope; the status said success
				return null;
			}
		}
	}
}
