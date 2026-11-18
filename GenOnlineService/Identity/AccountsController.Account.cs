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
		private string NameMessage(string? code) => code switch
		{
			Realtime.ErrorCodes.NameLength => T("Name.Length", Realtime.RealtimeHub.NameMinLength, Realtime.RealtimeHub.NameMaxLength),
			Realtime.ErrorCodes.NameWhitespace => T("Name.Whitespace"),
			Realtime.ErrorCodes.NameReserved => T("Name.Reserved"),
			Realtime.ErrorCodes.NameTaken => T("Name.Taken"),
			_ => T("Name.Invalid")
		};

		private async Task<ContentResult> AccountForm(int status, User user, bool renamed, string? typed, string? error)
		{
			string tag = Database.DisplayNames.FormatTag(user.NameTag);
			string fullName = Database.DisplayNames.Format(user.Username, user.NameTag);
			var body = new StringBuilder();
			if (renamed)
			{
				body.Append(PortalLook.Alert(T("Account.Renamed", fullName), success: true));
			}

			// Profile: the rename, the design's player-name field with the #tag as a fixed suffix, and the id and roles
			IReadOnlyCollection<string> roles = StaffRoles.RolesOf(user.ID);
			string badges = roles.Count == 0 ? PortalLook.Badge(T("Account.RolePlayer"), muted: true) : String.Concat(roles.Select(r => PortalLook.Badge(r)));
			var profile = new StringBuilder();
			string range = T("Name.Range", Realtime.RealtimeHub.NameMinLength, Realtime.RealtimeHub.NameMaxLength);
			profile.Append(PortalLook.Field("display_name", T("Field.PlayerName"),
				$"type=\"text\" name=\"{RenameField}\" autocomplete=\"nickname\" minlength=\"{Realtime.RealtimeHub.NameMinLength}\" maxlength=\"{Realtime.RealtimeHub.NameMaxLength}\" required" + (error == null ? String.Empty : " aria-invalid=\"true\""),
				typed ?? user.Username, suffix: "#" + tag, suffixLabel: T("Name.TagLabel", "#" + tag),
				hint: WebUtility.HtmlEncode($"{T("Name.Seen", fullName)} {range}"), error: error));
			profile.Append(PortalLook.Row(T("Account.PlayerId", user.ID.ToString(CultureInfo.InvariantCulture)), badges));
			body.Append(FormStart(AccountPath, AccountPath));
			body.Append(PortalLook.Card(T("Account.Profile.Title"), T("Account.Profile.Description") + " " + T("Account.TagHint"), profile.ToString(),
				PortalLook.Submit(T("Account.Save"), narrow: true)));
			body.Append("</form>");

			// Sign-in and security: the address and the second factor of a password account
			var security = new StringBuilder();
			bool hasPassword = user.PasswordHash != null;
			if (user.Email != null)
			{
				security.Append(PortalLook.Row(T("Field.Email"), $"<bdi>{WebUtility.HtmlEncode(user.Email)}</bdi>"
					+ PortalLook.Badge(T(user.EmailConfirmedAt != null ? "Account.Email.Confirmed" : "Account.Email.NotConfirmed"), muted: user.EmailConfirmedAt == null)));
			}
			if (hasPassword && m_settings.Password.Enabled)
			{
				string twoFactorLink = $"/profile/two_factor_auth?{ReturnUrlField}={Uri.EscapeDataString(AccountPath)}";
				security.Append(PortalLook.Row(T("TwoFactor.Title"), WebUtility.HtmlEncode(T(user.TwoFactorEnabled ? "TwoFactor.IsOn" : "TwoFactor.IsOff"))));
				security.Append(PortalLook.LinkButton(twoFactorLink, T(user.TwoFactorEnabled ? "TwoFactor.Manage" : "TwoFactor.Setup.Title"), narrow: true));
			}
			if (security.Length == 0)
			{
				security.Append(PortalLook.Text(WebUtility.HtmlEncode(T("Account.Security.LinkedOnly"))));
			}
			body.Append(PortalLook.Card(T("Account.Security.Title"), T("Account.Security.Description"), security.ToString()));

			// Linked sign-ins: the providers you can sign in with, by name; their ids stay private
			var linked = new StringBuilder();
			List<UserIdentity> identities;
			await using (AppDbContext db = await HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
			{
				identities = await db.Identities.AsNoTracking().Where(i => i.UserID == user.ID).OrderBy(i => i.CreatedAt).ToListAsync();
			}
			if (identities.Count == 0)
			{
				linked.Append(PortalLook.Text(WebUtility.HtmlEncode(T("Account.Linked.None"))));
			}
			foreach (UserIdentity identity in identities)
			{
				string name = m_settings.Providers.FirstOrDefault(p => p.Name == identity.Provider)?.DisplayName ?? UpstreamProviderSettings.DefaultDisplayName(identity.Provider);
				string when = identity.LastUsedAt is DateTime used
					? T("Account.Linked.WhenUsed", Day(identity.CreatedAt), Day(used))
					: T("Account.Linked.When", Day(identity.CreatedAt));
				linked.Append(PortalLook.Row(name, $"{PortalLook.ProviderName(identity.Provider, name)}<span class=\"gp-hint\">{WebUtility.HtmlEncode(when)}</span>"));
			}
			body.Append(PortalLook.Card(T("Account.Linked.Title"), T("Account.Linked.Description"), linked.ToString()));

			// Agreements (S19): what this account agreed to, each document's link, and the way to agree to what is owed
			if (m_settings.Agreements.Any)
			{
				body.Append(await AgreementsCard(user.ID));
			}

			// the top bar's account menu: the staff pages (/admin) for a holder of a staff role (the gate there checks the
			// permission again), and sign out
			bool staff = await m_passwords.IsStaff(user.ID);
			string signOut = FormStart("/users/sign_out", AccountPath) + $"<button type=\"submit\">{PortalLook.Icon("log-out")}{WebUtility.HtmlEncode(T("SignOut"))}</button></form>";
			var shell = new AccountShell(user.Username, "#" + tag, user.ID.ToString(CultureInfo.InvariantCulture), roles.Count == 0 ? T("Account.RolePlayer") : String.Join(", ", roles), staff, signOut);
			// only the sections that exist: Profile (the rest of the account pages come with later phases)
			(string, string, string)[] sections = [(AccountPath, T("Account.Profile.Title"), "user")];
			return new ContentResult
			{
				StatusCode = status,
				ContentType = "text/html; charset=utf-8",
				Content = PortalLook.AppDocument(T("Account.Profile.Title"), shell, sections, AccountPath, body.ToString(), Language, Site)
			};
		}

		private string Day(DateTime value) => AccountsText.Day(value, Culture);

		private async Task<string> AgreementsCard(Int64 userID)
		{
			Dictionary<string, UserAgreement> latest;
			IReadOnlyList<AgreementDocument> owed;
			await using (AppDbContext db = await HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
			{
				latest = await Agreements.LatestAsync(db, userID);
				owed = await Agreements.OwedAsync(db, m_settings.Agreements, userID);
			}

			var rows = new StringBuilder();
			foreach (AgreementDocument document in m_settings.Agreements.Required)
			{
				string label = T(document.Name == AgreementsSettings.Rules ? "Account.Agreements.Rules" : "Account.Agreements.Terms");
				var content = new StringBuilder();
				if (latest.TryGetValue(document.Name, out UserAgreement? agreed))
				{
					content.Append($"<bdi>{WebUtility.HtmlEncode(T("Account.Agreements.Agreed", agreed.Version, Day(agreed.AgreedAt)))}</bdi>");
					if (owed.Contains(document))
					{
						content.Append(PortalLook.Badge(T("Account.Agreements.NewVersion", document.Version!), muted: true));
					}
				}
				else
				{
					content.Append(PortalLook.Badge(T("Account.Agreements.NotAgreed"), muted: true));
				}
				content.Append(' ').Append(ExternalLink(document.Url!, T("Account.Agreements.Read")));
				rows.Append(PortalLook.Row(label, content.ToString()));
			}
			if (m_settings.Agreements.PrivacyUrl != null)
			{
				rows.Append(PortalLook.Muted(ExternalLink(m_settings.Agreements.PrivacyUrl, T("Agreements.PrivacyLink"))));
			}
			string? agree = owed.Count == 0 ? null : PortalLook.LinkButton(AgreementsUrl(AccountPath), T("Account.Agreements.Agree"), outline: false, narrow: true);
			return PortalLook.Card(T("Account.Agreements.Title"), T("Account.Agreements.Description"), rows.ToString(), agree);
		}
	}
}
