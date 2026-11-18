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
				return Page(400, T("Link.Invalid.Title"),
					PortalLook.Text(WebUtility.HtmlEncode(T("Confirm.Invalid")))
					+ PortalLook.Link(ConfirmationPendingUrl(AccountPath), T("Link.SendNew")), icon: "circle-alert");
			}

			return Page(200, T("Confirm.Done.Title"),
				PortalLook.Text(String.Format(Culture, WebUtility.HtmlEncode(T("Confirm.Done.Body")), $"<bdi>{WebUtility.HtmlEncode(user!.Email)}</bdi>"))
				+ PortalLook.LinkButton(AccountPath, T("Continue"), outline: false, icon: "arrow-right", large: true), icon: "circle-check");
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
			body.Append(PortalLook.Text(String.Format(Culture, WebUtility.HtmlEncode(T("Confirm.Pending.Body")), $"<strong><bdi>{WebUtility.HtmlEncode(user.Email)}</bdi></strong>")));
			// the design's CheckEmail: Continue and "Send the link again" side by side
			body.Append(FormStart(AccountMail.ConfirmationPath, target));
			body.Append("<div class=\"gp-actions\">").Append(PortalLook.LinkButton(target, T("Continue"), outline: false, large: true, fill: true));
			body.Append(PortalLook.Submit(T("Confirm.Resend"), outline: true, large: true, fill: true)).Append("</div></form>");
			return Page(status, T("CheckEmail.Title"), body.ToString(), T("Confirm.Pending.Description"), icon: "mail");
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
				return ConfirmationPendingForm(429, user, target, T("Mail.TooMany"), success: false);
			}
			return await m_mail.SendConfirmation(user, Culture)
				? ConfirmationPendingForm(200, user, target, T("Confirm.Resent"), success: true)
				: ConfirmationPendingForm(503, user, target, T("Mail.SendFailed"), success: false);
		}
	

		// Self-service password reset: the request never tells whether an account uses the address

		private const string ResetRequestPath = "/users/password/new";
		private const string ResetRequestAction = "/users/password";

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
			body.Append(PortalLook.Field("email", T("Field.Email"), "type=\"email\" name=\"email\" autocomplete=\"email\" required"));
			body.Append(PortalLook.Submit(T("Reset.Request.Submit"))).Append("</form>");
			body.Append(PortalLook.Link("/users/sign_in", T("SignIn.Title"), T("Reset.Request.Remember")));
			return Page(status, T("Reset.Request.Title"), body.ToString(), T("Reset.Request.Description"));
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
				return ResetRequestForm(429, T("Mail.TooMany"));
			}

			User? user = address.Length == 0 || address.Length > global::User.EmailMaxLength ? null : await m_users.FindByEmailAsync(address);
			// only an active account that signs in with a password; a failed send is logged and answered the same
			if (user != null && user.PasswordHash != null && user.State == UserStates.Active)
			{
				await m_mail.SendPasswordReset(user, Culture);
			}
			return Page(200, T("CheckEmail.Title"), PortalLook.Alert(T("Reset.Sent"), success: true) + PortalLook.Way("/users/sign_in", T("BackToSignIn")), icon: "mail");
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

		private ContentResult ResetLinkInvalid() => Page(400, T("Link.Invalid.Title"),
			PortalLook.Text(WebUtility.HtmlEncode(T("Reset.Invalid")))
			+ PortalLook.Link(ResetRequestPath, T("Link.SendNew")), icon: "circle-alert");

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
			body.Append(PortalLook.Field("password", T("Field.NewPassword"), $"type=\"password\" name=\"password\" autocomplete=\"new-password\" minlength=\"{m_settings.Password.MinLength}\" maxlength=\"{PasswordSignInSettings.MaximumLength}\" required"));
			body.Append(PortalLook.Submit(T("Reset.Submit"))).Append("</form>");
			return Page(status, T("Reset.Title"), body.ToString(), T("Reset.Description"));
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

			// sign-up's password rules (the same validators UserManager runs)
			string chosen = password ?? String.Empty;
			foreach (IPasswordValidator<User> validator in m_users.PasswordValidators)
			{
				IdentityResult valid = await validator.ValidateAsync(m_users, user, chosen);
				if (!valid.Succeeded)
				{
					return valid.Errors.First().Code switch
					{
						nameof(IdentityErrorDescriber.PasswordTooShort) => ResetPasswordForm(400, token!, T("SignUp.PasswordTooShort", m_settings.Password.MinLength)),
						MaximumLengthPasswordValidator.ErrorCode => ResetPasswordForm(400, token!, T("SignUp.PasswordTooLong", PasswordSignInSettings.MaximumLength)),
						_ => ResetPasswordForm(400, token!, T("Reset.Failed"))
					};
				}
			}

			// written only while the stamp the link was checked against is current: one of several racing requests wins
			if (!await m_passwords.RedeemReset(user, m_users.PasswordHasher.HashPassword(user, chosen)))
			{
				return ResetLinkInvalid();
			}
			await SessionRevocation.RevokeUser(HttpContext.RequestServices, user.ID, false, "password reset");
			AppMetrics.RecordAuthentication("password_reset", "password", "success");
			return Page(200, T("Reset.Done.Title"),
				PortalLook.Text(WebUtility.HtmlEncode(T("Reset.Done.Body")))
				+ PortalLook.LinkButton("/users/sign_in", T("SignIn.Submit"), outline: false, large: true), icon: "circle-check");
		}
	}
}
