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

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Net;
using System.Text;

namespace GenOnlineService.Identity
{
	// /profile (GitLab's user page): each environment's own player page (S12), so a player can see and fix their account on this environment
	// whether or not a central portal is up. Server-rendered in the sign-in pages' look, no script.
	public sealed partial class AccountsController
	{
		public const string AccountPath = "/profile";
		private const string AdminPath = "/admin/reports";
		private const string RenameField = "display_name";

		[HttpGet("~/profile")]
		public async Task<IActionResult> AccountPage([FromQuery] string? renamed)
		{
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInForAccount();
			}
			if (user.NameRequiredAt != null)
			{
				return LocalRedirect(WelcomeUrl(AccountPath));
			}
			return await AccountForm(200, user, renamed == "1", null, null);
		}

		// The rename, with the rules and the kept tag of PATCH /api/v1/me
		[HttpPost("~/profile")]
		public async Task<IActionResult> Rename([FromForm(Name = RenameField)] string? displayName)
		{
			if (!await ValidForm())
			{
				return Expired();
			}
			User? user = await CookieUser();
			if (user == null)
			{
				return SignInForAccount();
			}
			if (user.NameRequiredAt != null)
			{
				return LocalRedirect(WelcomeUrl(AccountPath));
			}

			Realtime.Result<Realtime.Empty> result = await m_names.RenameSelfAsync(user.ID, displayName);
			if (!result.Ok)
			{
				return await AccountForm(400, user, false, displayName, NameMessage(result.Code));
			}

			// the sign-in cookie carries the name, so it is reissued with the new one
			User? renamedUser = await m_users.FindByIdAsync(user.ID.ToString(CultureInfo.InvariantCulture));
			if (renamedUser != null)
			{
				await m_signIn.RefreshSignInAsync(renamedUser);
			}
			return LocalRedirect(AccountPath + "?renamed=1");
		}

		private IActionResult SignInForAccount()
			=> Challenge(new AuthenticationProperties { RedirectUri = AccountPath }, IdentityConstants.ApplicationScheme);

		// The message for a refused name, on the account page and the choose-a-name page alike
		private static string NameMessage(string? code) => code switch
		{
			Realtime.ErrorCodes.NameLength => $"Names must be {Realtime.RealtimeHub.NameMinLength} to {Realtime.RealtimeHub.NameMaxLength} characters.",
			Realtime.ErrorCodes.NameWhitespace => "Names cannot start or end with a space.",
			Realtime.ErrorCodes.NameReserved => "This name contains a reserved word.",
			Realtime.ErrorCodes.NameTaken => "This name is taken. Please choose another.",
			_ => "This name has characters that are not allowed."
		};

		private async Task<ContentResult> AccountForm(int status, User user, bool renamed, string? typed, string? error)
		{
			string tag = Database.DisplayNames.FormatTag(user.NameTag);
			string fullName = Database.DisplayNames.Format(user.Username, user.NameTag);
			var body = new StringBuilder();
			if (renamed)
			{
				body.Append(PortalLook.Alert($"Your name is now {fullName}.", success: true));
			}

			// Profile: who you are, and the rename
			IReadOnlyCollection<string> roles = StaffRoles.RolesOf(user.ID);
			string badges = roles.Count == 0 ? PortalLook.Badge("player", muted: true) : String.Concat(roles.Select(r => PortalLook.Badge(r)));
			var profile = new StringBuilder();
			profile.Append($"<div class=\"go-profile\">{PortalLook.Avatar(user.Username)}<div>");
			profile.Append($"<p class=\"go-profile-name\">{WebUtility.HtmlEncode(user.Username)}<span class=\"go-tag\">#{tag}</span></p>");
			profile.Append($"<div class=\"go-badges\"><span class=\"bb:text-xs bb:text-muted-foreground\">Player id {user.ID.ToString(CultureInfo.InvariantCulture)}</span>{badges}</div>");
			profile.Append("</div></div>");
			profile.Append(PortalLook.Separator());
			profile.Append(PortalLook.Field("display_name", "Display name", $"type=\"text\" name=\"{RenameField}\" autocomplete=\"nickname\" minlength=\"{Realtime.RealtimeHub.NameMinLength}\" maxlength=\"{Realtime.RealtimeHub.NameMaxLength}\" required" + (error == null ? String.Empty : " aria-invalid=\"true\""), typed ?? user.Username, icon: "user"));
			profile.Append(error == null
				? "<p class=\"go-field-hint\">Your tag follows the name: switching back to an earlier name brings back its tag.</p>"
				: $"<p class=\"go-field-error\" role=\"alert\">{WebUtility.HtmlEncode(error)}</p>");
			body.Append(FormStart(AccountPath, AccountPath));
			body.Append(PortalLook.Card("Profile", "The name other players see. The tag after the # keeps it unique.", profile.ToString(),
				PortalLook.Submit("Save changes", icon: "check", narrow: true)));
			body.Append("</form>");

			// Sign-in and security: the address and the second factor of a password account
			var security = new StringBuilder();
			bool hasPassword = user.PasswordHash != null;
			if (user.Email != null)
			{
				security.Append(PortalLook.Row("Email", WebUtility.HtmlEncode(user.Email)
					+ PortalLook.Badge(user.EmailConfirmedAt != null ? "confirmed" : "not confirmed", muted: user.EmailConfirmedAt == null)));
			}
			if (hasPassword && m_settings.Password.Enabled)
			{
				string twoFactorLink = $"/profile/two_factor_auth?{ReturnUrlField}={Uri.EscapeDataString(AccountPath)}";
				security.Append(PortalLook.Row("Two-factor sign-in", user.TwoFactorEnabled ? "Two-factor sign-in is on." : "Two-factor sign-in is off."));
				security.Append(PortalLook.LinkButton(twoFactorLink, user.TwoFactorEnabled ? "Manage two-factor sign-in" : "Set up two-factor sign-in", icon: "shield-check", narrow: true));
			}
			if (security.Length == 0)
			{
				security.Append(PortalLook.Text("You sign in with the accounts linked below."));
			}
			body.Append(PortalLook.Card("Sign-in and security", "How you sign in to Generals Online on this environment.", security.ToString()));

			// Linked sign-ins: the providers you can sign in with, by name; their ids stay private
			var linked = new StringBuilder();
			List<UserIdentity> identities;
			await using (AppDbContext db = await HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
			{
				identities = await db.Identities.AsNoTracking().Where(i => i.UserID == user.ID).OrderBy(i => i.CreatedAt).ToListAsync();
			}
			if (identities.Count == 0)
			{
				linked.Append(PortalLook.Text("No other sign-in is linked."));
			}
			foreach (UserIdentity identity in identities)
			{
				string name = m_settings.Providers.FirstOrDefault(p => p.Name == identity.Provider)?.DisplayName ?? UpstreamProviderSettings.DefaultDisplayName(identity.Provider);
				string when = "linked " + Day(identity.CreatedAt) + (identity.LastUsedAt is DateTime used ? ", last used " + Day(used) : String.Empty);
				linked.Append($"<div class=\"go-row\">{PortalLook.ProviderName(identity.Provider, name)}<span class=\"bb:text-xs bb:text-muted-foreground\">{WebUtility.HtmlEncode(when)}</span></div>");
			}
			body.Append(PortalLook.Card("Linked sign-ins", "The accounts you can sign in with.", linked.ToString()));

			// the staff pages (/admin) for a holder of a staff role; the page gate there checks the permission again
			string admin = await m_passwords.IsStaff(user.ID) ? PortalLook.LinkButton(AdminPath, "Admin", icon: "shield", narrow: true) : String.Empty;
			string signOut = FormStart("/users/sign_out", AccountPath) + PortalLook.Submit("Sign out", outline: true, icon: "log-out", narrow: true) + "</form>";
			return new ContentResult
			{
				StatusCode = status,
				ContentType = "text/html; charset=utf-8",
				Content = PortalLook.AppDocument("Account", fullName, admin + signOut, body.ToString())
			};
		}

		private static string Day(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
	}
}
