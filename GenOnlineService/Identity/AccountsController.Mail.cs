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
	// Account mail pages (S4), GitLab's routes: the email confirmation link and the page that waits for it. They exist
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
	}
}
