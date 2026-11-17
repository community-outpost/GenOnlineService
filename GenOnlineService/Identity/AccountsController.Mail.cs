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

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace GenOnlineService.Identity
{
	// Account mail pages (S4), GitLab's routes: the email confirmation link and the page that waits for it, and the
	// self-service password reset. They exist
	// only where mail is configured and password accounts are on; elsewhere they answer 404 and nothing waits.
	public sealed partial class AccountsController
	{
		private const string ConfirmationPendingPath = "/users/confirmation/new";
		private const string TooManyMails = "Too many mails were asked for. Please try again in a few minutes.";

		private static string ConfirmationPendingUrl(string target) => $"{ConfirmationPendingPath}?{ReturnUrlField}={Uri.EscapeDataString(target)}";

		// The address the request came from, for the per-client mail limits (the real client behind a trusted proxy)
		private string ClientAddressKey => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

		[HttpGet("~" + AccountMail.ConfirmationPath)]
		public async Task<IActionResult> ConfirmEmail([FromQuery(Name = AccountMail.ConfirmationTokenField)] string? token)
		{
			if (!m_mail.Enabled)
			{
				return NotFound();
			}

			User? user = AccountMail.TryUnpack(token, out Int64 userID, out string raw) ? await m_users.FindByIdAsync(userID.ToString()) : null;
			// Identity checks the purpose, the account, the security stamp and the 24 hours
			IdentityResult? confirmed = user == null || user.Email == null ? null : await m_users.ConfirmEmailAsync(user, raw);
			if (confirmed?.Succeeded != true)
			{
				return Page(400, "Link not valid",
					PortalLook.Text("This confirmation link is not valid or has expired.")
					+ PortalLook.Link(ConfirmationPendingUrl(AccountPath), "Send a new link"));
			}

			return Page(200, "Email address confirmed",
				PortalLook.Text($"Thanks, {WebUtility.HtmlEncode(user!.Email)} is confirmed.")
				+ PortalLook.LinkButton(AccountPath, "Continue", outline: false, icon: "arrow-right"));
		}

		// "Check your email": where /oauth/authorize sends a password account whose address is not confirmed yet
		[HttpGet("~" + ConfirmationPendingPath)]
		public async Task<IActionResult> ConfirmationPendingPage([FromQuery(Name = ReturnUrlField)] string? returnUrl)
		{
			if (!m_mail.Enabled)
			{
				return NotFound();
			}
			string target = SafeReturnUrl(returnUrl);
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInFirst(target);
			}
			if (!m_mail.MustConfirm(user))
			{
				return LocalRedirect(target);
			}
			return ConfirmationPendingForm(200, user, target, null, success: false);
		}

		private ContentResult ConfirmationPendingForm(int status, User user, string target, string? message, bool success)
		{
			var body = new System.Text.StringBuilder(message == null ? String.Empty : PortalLook.Alert(message, success));
			body.Append(PortalLook.Text($"We sent a link to <strong>{WebUtility.HtmlEncode(user.Email)}</strong>. Open it to confirm your address, then continue here."));
			body.Append(PortalLook.LinkButton(target, "Continue", outline: false, icon: "arrow-right"));
			body.Append(FormStart(AccountMail.ConfirmationPath, target));
			body.Append(PortalLook.Submit("Send the link again", outline: true, icon: "mail")).Append("</form>");
			return Page(status, "Check your email", body.ToString(), "Confirm your email address to play.");
		}

		// Sends the confirmation link again, a few times per account and per client address
		[HttpPost("~" + AccountMail.ConfirmationPath)]
		public async Task<IActionResult> ResendConfirmation([FromForm(Name = ReturnUrlField)] string? returnUrl)
		{
			if (!m_mail.Enabled)
			{
				return NotFound();
			}
			if (!await ValidForm())
			{
				return Expired();
			}

			string target = SafeReturnUrl(returnUrl);
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInFirst(target);
			}
			if (!m_mail.MustConfirm(user))
			{
				return LocalRedirect(target);
			}

			if (!m_mailLimiter.TryConsume(("confirmation:user:" + user.ID, AccountMailLimiter.PerAccount), ("confirmation:client:" + ClientAddressKey, AccountMailLimiter.PerClientAddress)))
			{
				return ConfirmationPendingForm(429, user, target, TooManyMails, success: false);
			}
			return await m_mail.SendConfirmation(user, Request)
				? ConfirmationPendingForm(200, user, target, "A new link is on its way.", success: true)
				: ConfirmationPendingForm(503, user, target, "The mail could not be sent. Please try again later.", success: false);
		}
	

		// Self-service password reset: the request never tells whether an account uses the address

		private const string ResetRequestPath = "/users/password/new";
		private const string ResetRequestAction = "/users/password";
		private const string ResetSent = "If an account uses this address, we sent it a link to choose a new password.";

		[HttpGet("~" + ResetRequestPath)]
		public IActionResult ResetRequestPage()
		{
			if (!m_mail.Enabled)
			{
				return NotFound();
			}
			return ResetRequestForm(200, null);
		}

		private ContentResult ResetRequestForm(int status, string? message)
		{
			var body = new System.Text.StringBuilder(Message(message));
			body.Append(FormStart(ResetRequestAction, AccountPath));
			body.Append(PortalLook.Field("email", "Email", "type=\"email\" name=\"email\" autocomplete=\"email\" required", icon: "mail"));
			body.Append(PortalLook.Submit("Send the link", icon: "mail")).Append("</form>");
			body.Append(PortalLook.Link("/users/sign_in", "Sign in", "Remember it?"));
			return Page(status, "Reset your password", body.ToString(), "We mail a link to choose a new password.");
		}

		[HttpPost("~" + ResetRequestAction)]
		public async Task<IActionResult> RequestReset([FromForm] string? email)
		{
			if (!m_mail.Enabled)
			{
				return NotFound();
			}
			if (!await ValidForm())
			{
				return Expired();
			}

			// limited by the address asked for, known or not, so the limit tells nothing either
			string address = (email ?? String.Empty).Trim();
			string key = address.Length > global::User.EmailMaxLength ? address.Substring(0, global::User.EmailMaxLength) : address;
			if (!m_mailLimiter.TryConsume(("reset:address:" + NameNormalizer.Normalize(key), AccountMailLimiter.PerAccount), ("reset:client:" + ClientAddressKey, AccountMailLimiter.PerClientAddress)))
			{
				return ResetRequestForm(429, TooManyMails);
			}

			User? user = address.Length == 0 || address.Length > global::User.EmailMaxLength ? null : await m_users.FindByEmailAsync(address);
			// only an active account that signs in with a password; a failed send is logged and answered the same
			if (user != null && user.PasswordHash != null && user.State == UserStates.Active)
			{
				await m_mail.SendPasswordReset(user, Request);
			}
			return Page(200, "Check your email", PortalLook.Alert(ResetSent, success: true) + PortalLook.Link("/users/sign_in", "Sign in"));
		}

		private const string ResetTokenField = AccountMail.ResetPasswordTokenField;

		// The account a reset link names, when the link is still good: its purpose, account, security stamp and hour
		private async Task<(User? User, string Token)> ResetLinkUser(string? packed)
		{
			if (!AccountMail.TryUnpack(packed, out Int64 userID, out string raw))
			{
				return (null, String.Empty);
			}
			User? user = await m_users.FindByIdAsync(userID.ToString());
			if (user == null || user.PasswordHash == null
				|| !await m_users.VerifyUserTokenAsync(user, m_users.Options.Tokens.PasswordResetTokenProvider, UserManager<User>.ResetPasswordTokenPurpose, raw))
			{
				return (null, String.Empty);
			}
			return (user, raw);
		}

		private ContentResult ResetLinkInvalid() => Page(400, "Link not valid",
			PortalLook.Text("This password reset link is not valid or has expired. Each link works once, for one hour.")
			+ PortalLook.Link(ResetRequestPath, "Send a new link"));

		[HttpGet("~" + AccountMail.ResetPasswordEditPath)]
		public async Task<IActionResult> ResetPasswordPage([FromQuery(Name = ResetTokenField)] string? token)
		{
			if (!m_mail.Enabled)
			{
				return NotFound();
			}
			(User? user, _) = await ResetLinkUser(token);
			return user == null ? ResetLinkInvalid() : ResetPasswordForm(200, token!, null);
		}

		private ContentResult ResetPasswordForm(int status, string packed, string? message)
		{
			var body = new System.Text.StringBuilder(Message(message));
			body.Append(FormStart(AccountMail.ResetPasswordEditPath, AccountPath)).Append(Hidden(ResetTokenField, packed));
			body.Append(PortalLook.Field("password", "New password", $"type=\"password\" name=\"password\" autocomplete=\"new-password\" minlength=\"{m_settings.Password.MinLength}\" maxlength=\"{PasswordSignInSettings.MaximumLength}\" required", icon: "lock-keyhole"));
			body.Append(PortalLook.Submit("Change password", icon: "lock-keyhole")).Append("</form>");
			return Page(status, "Choose a new password", body.ToString(), "Every other sign-in of the account ends.");
		}

		// The new password with sign-up's rules; the security stamp changes (the link and website cookies stop working),
		// every sign-in and refresh token is revoked, the lockout ends and the reset is audited
		[HttpPost("~" + AccountMail.ResetPasswordEditPath)]
		public async Task<IActionResult> ResetPassword([FromForm(Name = ResetTokenField)] string? token, [FromForm] string? password)
		{
			if (!m_mail.Enabled)
			{
				return NotFound();
			}
			if (!await ValidForm())
			{
				return Expired();
			}

			(User? user, string raw) = await ResetLinkUser(token);
			if (user == null)
			{
				return ResetLinkInvalid();
			}

			IdentityResult reset = await m_users.ResetPasswordAsync(user, raw, password ?? String.Empty);
			if (!reset.Succeeded)
			{
				return reset.Errors.First().Code switch
				{
					nameof(IdentityErrorDescriber.PasswordTooShort) => ResetPasswordForm(400, token!, $"Passwords must be at least {m_settings.Password.MinLength} characters."),
					MaximumLengthPasswordValidator.ErrorCode => ResetPasswordForm(400, token!, $"Passwords must be at most {PasswordSignInSettings.MaximumLength} characters."),
					nameof(IdentityErrorDescriber.InvalidToken) => ResetLinkInvalid(),
					_ => ResetPasswordForm(400, token!, "The password could not be changed.")
				};
			}

			await m_passwords.RecordSelfReset(user.ID);
			await SessionRevocation.RevokeUser(HttpContext.RequestServices, user.ID, false, "password reset");
			AppMetrics.RecordAuthentication("password_reset", "password", "success");
			return Page(200, "Your password is changed",
				PortalLook.Text("Sign in with your new password. Every other sign-in of this account has ended.")
				+ PortalLook.LinkButton("/users/sign_in", "Sign in", outline: false, icon: "log-in"));
		}
	}
}
