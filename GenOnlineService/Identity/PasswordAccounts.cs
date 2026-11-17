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

using GenOnlineService.Realtime;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;

namespace GenOnlineService.Identity
{
	public sealed record PasswordAccountResult(User? User, string? Error)
	{
		public static PasswordAccountResult Refused(string error) => new(null, error);
	}

	// Email + password accounts (Accounts:password): sign-up checks and creates, sign-in checks the password with lockout.
	// Signing the browser in is the caller's, so a page can still ask for a second factor first.
	public sealed class PasswordAccounts
	{
		public const string ErrorHoneypot = "honeypot";
		public const string ErrorInvalidEmail = "invalid_email";
		public const string ErrorEmailDenied = "email_denied";
		public const string ErrorEmailTaken = "email_taken";
		public const string ErrorNameInvalid = "name_invalid";
		public const string ErrorNameLength = "name_length";
		public const string ErrorNameReserved = "name_reserved";
		public const string ErrorPasswordTooShort = "password_too_short";
		public const string ErrorPasswordTooLong = "password_too_long";
		public const string ErrorSignUpFailed = "sign_up_failed";
		// a wrong password, an unknown email, an account without a password and a locked account look the same
		public const string ErrorWrongCredentials = "wrong_credentials";
		public const string ErrorBlocked = UpstreamAccounts.ErrorBlocked;
		public const string ErrorDeactivated = UpstreamAccounts.ErrorDeactivated;
		public const string ErrorTwoFactorRequired = "two_factor_required";

		// The authentication method of a cookie signed in with a password alone
		public const string PasswordMethod = "password";

		private static readonly ILogger s_log = AppLog.For(typeof(PasswordAccounts));
		private readonly PasswordSignInSettings m_settings;
		private readonly UserManager<User> m_users;
		private readonly SignInManager<User> m_signIn;
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public PasswordAccounts(AccountsSettings settings, UserManager<User> users, SignInManager<User> signIn, IDbContextFactory<AppDbContext> dbFactory)
		{
			m_settings = settings.Password;
			m_users = users;
			m_signIn = signIn;
			m_dbFactory = dbFactory;
		}

		// Staff hold a role that grants permissions or a rank; read from the database, not the few-second cache, so a
		// fresh grant counts at once, and a failed read fails the request instead of passing as "not staff"
		public async Task<bool> IsStaff(Int64 userID)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			List<string> roles = await db.UserRoles.Where(r => r.UserID == userID).Select(r => r.Role).ToListAsync();
			return roles.Any(StaffRoles.IsStaff);
		}

		// Staff signed in with a password alone must set up two-factor sign-in before any token is issued
		public async Task<bool> MustSetUpTwoFactor(User user, System.Security.Claims.ClaimsPrincipal cookie)
		{
			return !user.TwoFactorEnabled
				&& cookie.FindFirst(System.Security.Claims.ClaimTypes.AuthenticationMethod)?.Value == PasswordMethod
				&& await IsStaff(user.ID);
		}

		public async Task<PasswordAccountResult> SignUp(string? displayName, string? email, string? password, string? honeypot)
		{
			if (m_settings.Honeypot && !String.IsNullOrEmpty(honeypot))
			{
				s_log.LogInformation("Sign-up refused: the honeypot field was filled");
				return PasswordAccountResult.Refused(ErrorHoneypot);
			}

			string name = (displayName ?? String.Empty).Trim();
			if (ProfileActions.CheckNameRules(name) is Failure nameFailure)
			{
				return PasswordAccountResult.Refused(nameFailure.Code == ErrorCodes.NameLength ? ErrorNameLength : ErrorNameInvalid);
			}
			if (ProfileActions.ReservedWordIn(name) != null)
			{
				return PasswordAccountResult.Refused(ErrorNameReserved);
			}

			string? address = ParseEmail(email);
			if (address == null)
			{
				return PasswordAccountResult.Refused(ErrorInvalidEmail);
			}
			if (IsDenied(address))
			{
				return PasswordAccountResult.Refused(ErrorEmailDenied);
			}

			// the address and the account are one row, so a failed sign-up leaves nothing behind
			var user = new User { Username = name, State = UserStates.Active, Email = address, EmailNormalized = NameNormalizer.Normalize(address) };
			IdentityResult created = await m_users.CreateAsync(user, password ?? String.Empty);
			if (!created.Succeeded)
			{
				string code = created.Errors.First().Code;
				return PasswordAccountResult.Refused(code switch
				{
					nameof(IdentityErrorDescriber.PasswordTooShort) => ErrorPasswordTooShort,
					MaximumLengthPasswordValidator.ErrorCode => ErrorPasswordTooLong,
					GoUserValidator.DuplicateEmail => ErrorEmailTaken,
					_ => ErrorSignUpFailed
				});
			}

			s_log.LogInformation("Created user {UserId} from a password sign-up", user.ID);
			return new PasswordAccountResult(user, null);
		}

		public async Task<PasswordAccountResult> SignIn(string? email, string? password)
		{
			string? address = ParseEmail(email);
			User? user = address == null ? null : await m_users.FindByEmailAsync(address);
			if (user == null || String.IsNullOrEmpty(password))
			{
				return PasswordAccountResult.Refused(ErrorWrongCredentials);
			}

			// counts a wrong password toward the lockout, refuses while locked, resets the count on success
			SignInResult checkedPassword = await m_signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
			if (!checkedPassword.Succeeded)
			{
				return PasswordAccountResult.Refused(ErrorWrongCredentials);
			}

			// the account's state is told only to whoever knows its password
			if (user.State == UserStates.Blocked)
			{
				return PasswordAccountResult.Refused(ErrorBlocked);
			}
			if (user.State == UserStates.Deactivated)
			{
				return PasswordAccountResult.Refused(ErrorDeactivated);
			}

			if (await m_users.GetTwoFactorEnabledAsync(user))
			{
				return new PasswordAccountResult(user, ErrorTwoFactorRequired);
			}

			return new PasswordAccountResult(user, null);
		}

		// The address as typed (trimmed) when it is one plain address with a dotted domain, else null
		private static string? ParseEmail(string? email)
		{
			string trimmed = (email ?? String.Empty).Trim();
			if (trimmed.Length == 0 || trimmed.Length > User.EmailMaxLength || !MailAddress.TryCreate(trimmed, out MailAddress? parsed)
				|| !String.Equals(parsed.Address, trimmed, StringComparison.Ordinal) || !parsed.Host.Contains('.') || parsed.Host.EndsWith('.'))
			{
				return null;
			}
			return trimmed;
		}

		// The domain or any of its subdomains is on the deny list
		private bool IsDenied(string address)
		{
			string domain = address.Substring(address.LastIndexOf('@') + 1).ToLowerInvariant();
			return m_settings.EmailDomainDenyList.Any(d => domain == d || domain.EndsWith("." + d, StringComparison.Ordinal));
		}
	}

	// Identity's own user validator assumes unique user names; display names are not (Name#tag), so only the email
	// address, when there is one, must belong to no other account
	public sealed class GoUserValidator : IUserValidator<User>
	{
		public const string DuplicateEmail = nameof(IdentityErrorDescriber.DuplicateEmail);

		public async Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user)
		{
			if (user.Email == null)
			{
				return IdentityResult.Success;
			}

			User? holder = await manager.FindByEmailAsync(user.Email);
			return holder != null && holder.ID != user.ID
				? IdentityResult.Failed(new IdentityError { Code = DuplicateEmail, Description = "An account with this email address already exists." })
				: IdentityResult.Success;
		}
	}
}
