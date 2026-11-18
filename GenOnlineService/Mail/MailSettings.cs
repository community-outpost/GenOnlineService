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

using System.Net.Mail;

namespace GenOnlineService.Mail
{
	// How mail leaves this service, GitLab style: one delivery_method and its own subsection
	public enum DeliveryMethod
	{
		None,
		Smtp,
		Brevo,
		Resend,
		Mailgun,
		Cloudflare
	}

	public enum SmtpSecurity
	{
		// plain connection upgraded with STARTTLS, which the server must offer (port 587)
		StartTls,
		// TLS from the first byte (port 465)
		Tls,
		// no encryption, for a relay on the same host or network only
		None
	}

	public sealed class SmtpSettings
	{
		public string Host { get; init; } = String.Empty;
		public int Port { get; init; } = 587;
		public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;
		public string? User { get; init; }
		public string? Password { get; init; }
	}

	// Mail: the sender of account mail (verification, password reset). Off (none) unless the operator picks a method; the
	// API keys and passwords come from the secret store like every other secret and are never logged.
	public sealed class MailSettings
	{
		public const string DefaultFromName = "Generals Online";

		public DeliveryMethod DeliveryMethod { get; init; } = DeliveryMethod.None;
		public string From { get; init; } = String.Empty;
		public string FromName { get; init; } = DefaultFromName;
		public string? ReplyTo { get; init; }
		public int TimeoutSeconds { get; init; } = 15;

		public SmtpSettings Smtp { get; init; } = new();
		// brevo, resend and mailgun: the account's API key; cloudflare: an API token allowed to send
		public string? ApiKey { get; init; }
		// mailgun: the sending domain and its region (us or eu)
		public string? MailgunDomain { get; init; }
		public bool MailgunEu { get; init; }
		// cloudflare: the account that owns the sending domain
		public string? CloudflareAccountID { get; init; }

		public bool Enabled => DeliveryMethod != DeliveryMethod.None;

		public static MailSettings Read(IConfiguration config)
		{
			IConfigurationSection section = config.GetSection("Mail");
			string methodText = section["delivery_method"] ?? "none";
			DeliveryMethod method = methodText.Trim().ToLowerInvariant() switch
			{
				"none" => DeliveryMethod.None,
				"smtp" => DeliveryMethod.Smtp,
				"brevo" => DeliveryMethod.Brevo,
				"resend" => DeliveryMethod.Resend,
				"mailgun" => DeliveryMethod.Mailgun,
				"cloudflare" => DeliveryMethod.Cloudflare,
				_ => throw new InvalidOperationException($"Mail:delivery_method must be none, smtp, brevo, resend, mailgun or cloudflare, not \"{methodText}\".")
			};
			if (method == DeliveryMethod.None)
			{
				return new MailSettings();
			}

			string from = (section["from"] ?? String.Empty).Trim();
			if (!IsAddress(from))
			{
				throw new InvalidOperationException("Mail:from must be the email address mail is sent from.");
			}
			string? replyTo = Blank(section["reply_to"]);
			if (replyTo != null && !IsAddress(replyTo))
			{
				throw new InvalidOperationException("Mail:reply_to must be an email address.");
			}
			string fromName = Blank(section["from_name"]) ?? DefaultFromName;
			if (fromName.IndexOfAny(new[] { '\r', '\n', '"', '<', '>' }) >= 0)
			{
				throw new InvalidOperationException("Mail:from_name cannot hold line breaks, quotes or angle brackets.");
			}
			int timeout = section.GetValue<int?>("timeout_seconds") ?? 15;
			if (timeout < 1 || timeout > 120)
			{
				throw new InvalidOperationException("Mail:timeout_seconds must be between 1 and 120.");
			}

			IConfigurationSection own = section.GetSection(methodText.Trim().ToLowerInvariant());
			var settings = new MailSettings
			{
				DeliveryMethod = method,
				From = from,
				FromName = fromName,
				ReplyTo = replyTo,
				TimeoutSeconds = timeout
			};

			switch (method)
			{
				case DeliveryMethod.Smtp:
					return settings.With(smtp: ReadSmtp(own));
				case DeliveryMethod.Brevo:
				case DeliveryMethod.Resend:
					return settings.With(apiKey: Required(own, "api_key", method));
				case DeliveryMethod.Mailgun:
				{
					string domain = Required(own, "domain", method);
					string region = (Blank(own["region"]) ?? "us").ToLowerInvariant();
					if (region != "us" && region != "eu")
					{
						throw new InvalidOperationException("Mail:mailgun:region must be us or eu.");
					}
					return settings.With(apiKey: Required(own, "api_key", method), mailgunDomain: domain, mailgunEu: region == "eu");
				}
				case DeliveryMethod.Cloudflare:
					return settings.With(apiKey: Required(own, "api_token", method), cloudflareAccountID: Required(own, "account_id", method));
				default:
					return settings;
			}
		}

		private MailSettings With(SmtpSettings? smtp = null, string? apiKey = null, string? mailgunDomain = null, bool mailgunEu = false, string? cloudflareAccountID = null) => new()
		{
			DeliveryMethod = DeliveryMethod,
			From = From,
			FromName = FromName,
			ReplyTo = ReplyTo,
			TimeoutSeconds = TimeoutSeconds,
			Smtp = smtp ?? Smtp,
			ApiKey = apiKey,
			MailgunDomain = mailgunDomain,
			MailgunEu = mailgunEu,
			CloudflareAccountID = cloudflareAccountID
		};

		private static SmtpSettings ReadSmtp(IConfigurationSection own)
		{
			string host = Required(own, "host", DeliveryMethod.Smtp);
			string securityText = (Blank(own["security"]) ?? "starttls").ToLowerInvariant();
			SmtpSecurity security = securityText switch
			{
				"starttls" => SmtpSecurity.StartTls,
				"tls" => SmtpSecurity.Tls,
				"none" => SmtpSecurity.None,
				_ => throw new InvalidOperationException("Mail:smtp:security must be starttls, tls or none.")
			};
			int port = own.GetValue<int?>("port") ?? (security == SmtpSecurity.Tls ? 465 : 587);
			if (port < 1 || port > 65535)
			{
				throw new InvalidOperationException("Mail:smtp:port must be between 1 and 65535.");
			}
			string? user = Blank(own["user"]);
			string? password = Blank(own["password"]);
			if ((user == null) != (password == null))
			{
				throw new InvalidOperationException("Mail:smtp needs both user and password, or neither.");
			}
			return new SmtpSettings { Host = host, Port = port, Security = security, User = user, Password = password };
		}

		private static string Required(IConfigurationSection own, string key, DeliveryMethod method)
			=> Blank(own[key]) ?? throw new InvalidOperationException($"Mail:{method.ToString().ToLowerInvariant()}:{key} is required when Mail:delivery_method is {method.ToString().ToLowerInvariant()}.");

		private static string? Blank(string? value) => String.IsNullOrWhiteSpace(value) ? null : value.Trim();

		internal static bool IsAddress(string? value)
		{
			if (String.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '\r', '\n', '<', '>', ' ', ',' }) >= 0)
			{
				return false;
			}
			return MailAddress.TryCreate(value, out MailAddress? parsed) && parsed.Address == value;
		}
	}
}
