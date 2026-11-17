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

using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System.Net.Sockets;

namespace GenOnlineService.Mail
{
	// SMTP through MailKit: one connection per mail (account mail is rare, a pool would hold idle connections), STARTTLS,
	// TLS or none as Mail:smtp:security says, the sign-in only when a user is set. Messages never name the password.
	public sealed class SmtpEmailSender : IEmailSender
	{
		private readonly MailSettings m_settings;

		public SmtpEmailSender(MailSettings settings)
		{
			m_settings = settings;
		}

		public bool Enabled => true;

		// The mail as it goes out: one From with the display name, an optional Reply-To, the text part before the HTML part
		// (readers show the last part they can, so HTML wins where it can be shown)
		public static MimeMessage BuildMessage(MailSettings settings, EmailMessage message)
		{
			var mime = new MimeMessage();
			mime.From.Add(new MailboxAddress(settings.FromName, settings.From));
			mime.To.Add(MailboxAddress.Parse(message.To));
			if (settings.ReplyTo != null)
			{
				mime.ReplyTo.Add(MailboxAddress.Parse(settings.ReplyTo));
			}
			mime.Subject = message.Subject;
			mime.Body = new MultipartAlternative
			{
				new TextPart("plain") { Text = message.Text },
				new TextPart("html") { Text = message.Html }
			};
			return mime;
		}

		public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
		{
			if (!MailSettings.IsAddress(message.To))
			{
				throw new EmailDeliveryException("SMTP: the recipient is not one email address.");
			}

			MimeMessage mime = BuildMessage(m_settings, message);
			SecureSocketOptions security = m_settings.Smtp.Security switch
			{
				SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
				SmtpSecurity.None => SecureSocketOptions.None,
				_ => SecureSocketOptions.StartTls
			};

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(m_settings.TimeoutSeconds));
			using var client = new SmtpClient { Timeout = m_settings.TimeoutSeconds * 1000 };
			try
			{
				await client.ConnectAsync(m_settings.Smtp.Host, m_settings.Smtp.Port, security, timeout.Token);
				if (m_settings.Smtp.User != null)
				{
					await client.AuthenticateAsync(m_settings.Smtp.User, m_settings.Smtp.Password!, timeout.Token);
				}
				await client.SendAsync(mime, timeout.Token);
				await client.DisconnectAsync(true, timeout.Token);
			}
			catch (Exception ex) when (ex is SocketException || ex is IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
			{
				throw new EmailDeliveryException($"SMTP server {m_settings.Smtp.Host}:{m_settings.Smtp.Port} could not be reached ({ex.GetType().Name}).", ex);
			}
			catch (AuthenticationException ex)
			{
				throw new EmailDeliveryException($"SMTP server {m_settings.Smtp.Host} refused the sign-in of {m_settings.Smtp.User}.", ex);
			}
			catch (SmtpCommandException ex)
			{
				throw new EmailDeliveryException($"SMTP server {m_settings.Smtp.Host} refused the mail: {(int)ex.StatusCode} {ex.ErrorCode}.", ex);
			}
			catch (Exception ex) when (ex is ProtocolException || ex is SslHandshakeException || ex is ServiceNotConnectedException)
			{
				throw new EmailDeliveryException($"SMTP server {m_settings.Smtp.Host} broke off the exchange ({ex.GetType().Name}).", ex);
			}
		}
	}
}
