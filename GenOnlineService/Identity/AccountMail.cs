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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace GenOnlineService.Identity
{
	// Identity's data protection tokens, one provider per purpose so each keeps its own lifetime. Both carry the security
	// stamp, so a password change ends every link sent before it, and a used reset link (which rotates the stamp) never works twice.
	public sealed class EmailConfirmationTokenProviderOptions : DataProtectionTokenProviderOptions
	{
		public const string ProviderName = "go_email_confirmation";

		public EmailConfirmationTokenProviderOptions()
		{
			Name = ProviderName;
			TokenLifespan = TimeSpan.FromHours(24);
		}
	}

	public sealed class EmailConfirmationTokenProvider : DataProtectorTokenProvider<User>
	{
		public EmailConfirmationTokenProvider(IDataProtectionProvider dataProtectionProvider, IOptions<EmailConfirmationTokenProviderOptions> options, ILogger<DataProtectorTokenProvider<User>> logger)
			: base(dataProtectionProvider, options, logger)
		{
		}
	}

	public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
	{
		public const string ProviderName = "go_password_reset";

		public PasswordResetTokenProviderOptions()
		{
			Name = ProviderName;
			TokenLifespan = TimeSpan.FromHours(1);
		}
	}

	public sealed class PasswordResetTokenProvider : DataProtectorTokenProvider<User>
	{
		public PasswordResetTokenProvider(IDataProtectionProvider dataProtectionProvider, IOptions<PasswordResetTokenProviderOptions> options, ILogger<DataProtectorTokenProvider<User>> logger)
			: base(dataProtectionProvider, options, logger)
		{
		}
	}

	// How often account mail may be asked for, in any 15-minute window: per account or address, and per client address.
	// A mail costs the operator money and lands in someone's inbox, so these limits are the service's (the proxy limits
	// requests, not mails). Per copy of the service, in memory, like the report limit.
	public sealed class AccountMailLimiter
	{
		public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
		public const int PerAccount = 3;
		public const int PerClientAddress = 10;

		private static readonly TimeSpan s_pruneInterval = TimeSpan.FromMinutes(5);
		private readonly Dictionary<string, Queue<DateTime>> m_recent = new(StringComparer.Ordinal);
		private DateTime m_nextPrune = DateTime.MinValue;

		// All or nothing: a request counts against every key only when each still has room
		public bool TryConsume(params (string Key, int Limit)[] keys)
		{
			DateTime now = DateTime.UtcNow;
			lock (m_recent)
			{
				Prune(now);
				foreach ((string key, int limit) in keys)
				{
					if (m_recent.TryGetValue(key, out Queue<DateTime>? recent))
					{
						while (recent.Count > 0 && now - recent.Peek() >= Window)
						{
							recent.Dequeue();
						}
						if (recent.Count >= limit)
						{
							return false;
						}
					}
				}
				foreach ((string key, _) in keys)
				{
					if (!m_recent.TryGetValue(key, out Queue<DateTime>? recent))
					{
						m_recent[key] = recent = new Queue<DateTime>();
					}
					recent.Enqueue(now);
				}
				return true;
			}
		}

		private void Prune(DateTime now)
		{
			if (now < m_nextPrune)
			{
				return;
			}
			m_nextPrune = now + s_pruneInterval;
			foreach (string key in m_recent.Where(e => e.Value.Count == 0 || now - e.Value.Last() >= Window).Select(e => e.Key).ToList())
			{
				m_recent.Remove(key);
			}
		}
	}

	// Account mail (S4): the confirmation of a password account's address and the self-service password reset link.
	// Without Mail:delivery_method nothing is sent and nothing waits for a confirmation.
	public sealed class AccountMail
	{
		public const string ConfirmationPath = "/users/confirmation";
		public const string ConfirmationTokenField = "confirmation_token";
		public const string ResetPasswordEditPath = "/users/password/edit";
		public const string ResetPasswordTokenField = "reset_password_token";

		private static readonly ILogger s_log = AppLog.For(typeof(AccountMail));
		private readonly IEmailSender m_sender;
		private readonly AccountsSettings m_settings;
		private readonly UserManager<User> m_users;

		public AccountMail(IEmailSender sender, AccountsSettings settings, UserManager<User> users)
		{
			m_sender = sender;
			m_settings = settings;
			m_users = users;
		}

		// Mail is configured and there are password accounts to send it for
		public bool Enabled => m_sender.Enabled && m_settings.Password.Enabled;

		// A password account waits for its address to be confirmed before any token; provider accounts never do
		public bool MustConfirm(User user) => Enabled && user.PasswordHash != null && user.Email != null && user.EmailConfirmedAt == null;

		public async Task<bool> SendConfirmation(User user, HttpRequest request)
		{
			string token = await m_users.GenerateEmailConfirmationTokenAsync(user);
			return await Send(EmailTemplates.Confirmation, user, Link(request, ConfirmationPath, ConfirmationTokenField, Pack(user.ID, token)));
		}

		public async Task<bool> SendPasswordReset(User user, HttpRequest request)
		{
			string token = await m_users.GeneratePasswordResetTokenAsync(user);
			return await Send(EmailTemplates.PasswordReset, user, Link(request, ResetPasswordEditPath, ResetPasswordTokenField, Pack(user.ID, token)));
		}

		private async Task<bool> Send(string template, User user, string link)
		{
			// a password account always has a name; the address stands in while a name is still owed
			string name = user.NameRequiredAt == null && user.Username.Length > 0 ? Database.DisplayNames.Format(user.Username, user.NameTag) : user.Email!;
			EmailMessage message = EmailTemplates.Render(template, user.Email!, new Dictionary<string, string> { ["name"] = name, ["link"] = link });
			try
			{
				await m_sender.SendAsync(message);
				return true;
			}
			catch (Exception ex)
			{
				// the reason only: the address and the link stay out of the log
				s_log.LogError("Sending the {Template} mail of user {UserId} failed: {Reason}", template, user.ID, ex.Message);
				return false;
			}
		}

		// Absolute on the host the request came in on, or on Accounts:issuer when the operator set the public address
		private string Link(HttpRequest request, string path, string field, string token)
		{
			string origin = !String.IsNullOrWhiteSpace(m_settings.Issuer)
				? m_settings.Issuer.Trim().TrimEnd('/')
				: $"{request.Scheme}://{request.Host}{request.PathBase}";
			return $"{origin}{path}?{field}={Uri.EscapeDataString(token)}";
		}

		// The link carries one opaque value, as GitLab's: the account id and Identity's token together
		public static string Pack(Int64 userID, string token)
			=> WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(userID.ToString(CultureInfo.InvariantCulture) + ":" + token));

		public static bool TryUnpack(string? packed, out Int64 userID, out string token)
		{
			userID = 0;
			token = String.Empty;
			if (String.IsNullOrEmpty(packed) || packed.Length > 2048)
			{
				return false;
			}
			string text;
			try
			{
				text = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(packed));
			}
			catch (FormatException)
			{
				return false;
			}
			int colon = text.IndexOf(':');
			if (colon <= 0 || !Int64.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out userID) || colon == text.Length - 1)
			{
				return false;
			}
			token = text.Substring(colon + 1);
			return true;
		}
	}
}
