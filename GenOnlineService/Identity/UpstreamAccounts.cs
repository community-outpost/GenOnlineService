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

using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using System.Globalization;
using System.Security.Claims;
using System.Text;

namespace GenOnlineService.Identity
{
	public sealed record UpstreamSignInResult(User? User, string? Error)
	{
		public static UpstreamSignInResult Refused(string error) => new(null, error);
	}

	// GitLab OmniAuth behaviour: a known identity signs in, a trusted link claim or verified email links, otherwise sign-up
	public sealed class UpstreamAccounts
	{
		public const string ErrorNoSubject = "no_subject";
		public const string ErrorSignUpDisabled = "sign_up_disabled";
		public const string ErrorBlocked = "blocked";
		public const string ErrorDeactivated = "deactivated";

		private static readonly ILogger s_log = AppLog.For(typeof(UpstreamAccounts));
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public UpstreamAccounts(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		public async Task<UpstreamSignInResult> SignIn(UpstreamProviderSettings provider, ClaimsPrincipal external)
		{
			string? subject = external.GetClaim(OpenIddictConstants.Claims.Subject) ?? external.FindFirst(ClaimTypes.NameIdentifier)?.Value;
			if (String.IsNullOrEmpty(subject) || subject.Length > 128)
			{
				return UpstreamSignInResult.Refused(ErrorNoSubject);
			}

			string? externName = Truncate(external.GetClaim(OpenIddictConstants.Claims.PreferredUsername) ?? external.GetClaim(OpenIddictConstants.Claims.Name), 128);

			try
			{
				return await SignInOnce(provider, external, subject, externName);
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				// a concurrent first sign-in created the identity or took the name; the second attempt finds it
				return await SignInOnce(provider, external, subject, externName);
			}
		}

		private async Task<UpstreamSignInResult> SignInOnce(UpstreamProviderSettings provider, ClaimsPrincipal external, string subject, string? externName)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			DateTime now = DateTime.UtcNow;

			UserIdentity? identity = await db.Identities.AsTracking().FirstOrDefaultAsync(i => i.Provider == provider.Name && i.ExternUid == subject);
			User? user = identity == null ? null : await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == identity.UserID);

			if (user == null)
			{
				// a new user and its identity commit together, so a failed link never leaves an orphan account
				await using var tx = await db.Database.BeginTransactionAsync();
				user = await FindLinkTarget(db, provider, external);

				if (user == null)
				{
					if (!provider.AllowSignUp)
					{
						return UpstreamSignInResult.Refused(ErrorSignUpDisabled);
					}

					user = new User
					{
						Username = await UniqueUsername(db, externName),
						State = UserStates.Active,
						CreatedAt = now,
						UpdatedAt = now
					};
					user.UsernameNormalized = NameNormalizer.Normalize(user.Username);
					db.Users.Add(user);
					await db.SaveChangesAsync();
					s_log.LogInformation("Created user {UserId} from {Provider} sign-up", user.ID, provider.Name);
				}

				identity = new UserIdentity { UserID = user.ID, Provider = provider.Name, ExternUid = subject, CreatedAt = now };
				db.Identities.Add(identity);
				await db.SaveChangesAsync();
				await tx.CommitAsync();
			}

			if (user.State == UserStates.Blocked)
			{
				return UpstreamSignInResult.Refused(ErrorBlocked);
			}

			if (user.State == UserStates.Deactivated)
			{
				return UpstreamSignInResult.Refused(ErrorDeactivated);
			}

			identity.ExternUsername = externName;
			identity.LastUsedAt = now;
			user.LastSignInAt = now;
			await db.SaveChangesAsync();

			if (provider.Trusted)
			{
				await ApplyTrustedClaims(db, provider, user.ID, GroupsOf(provider, external));
			}

			return new UpstreamSignInResult(user, null);
		}

		// Link targets: only a trusted provider may name an existing account, a verified email only where the operator allows it
		private static async Task<User?> FindLinkTarget(AppDbContext db, UpstreamProviderSettings provider, ClaimsPrincipal external)
		{
			if (provider.Trusted
				&& Int64.TryParse(external.GetClaim(provider.LinkClaim), NumberStyles.None, CultureInfo.InvariantCulture, out Int64 linkedID)
				&& !await db.Identities.AnyAsync(i => i.UserID == linkedID && i.Provider == provider.Name))
			{
				User? linked = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == linkedID);
				if (linked != null)
				{
					s_log.LogInformation("Linking {Provider} identity to existing user {UserId} through {Claim}", provider.Name, linkedID, provider.LinkClaim);
					return linked;
				}
			}

			string? email = external.GetClaim(OpenIddictConstants.Claims.Email);
			if (provider.AutoLinkVerifiedEmail && !String.IsNullOrEmpty(email)
				&& String.Equals(external.GetClaim(OpenIddictConstants.Claims.EmailVerified), "true", StringComparison.OrdinalIgnoreCase))
			{
				string normalized = NameNormalizer.Normalize(email);
				User? byEmail = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.EmailNormalized == normalized);
				if (byEmail != null && !await db.Identities.AnyAsync(i => i.UserID == byEmail.ID && i.Provider == provider.Name))
				{
					return byEmail;
				}
			}

			return null;
		}

		public static HashSet<string> GroupsOf(UpstreamProviderSettings provider, ClaimsPrincipal external)
		{
			return external.FindAll(provider.GroupsClaim).Select(c => c.Value.TrimStart('/')).ToHashSet(StringComparer.Ordinal);
		}

		// Rewrites the provider's role and ban rows from its groups; returns what changed
		public static async Task<Database.Upstream.Changes> ApplyTrustedClaims(AppDbContext db, UpstreamProviderSettings provider, Int64 userID, IReadOnlySet<string> groups)
		{
			List<string> roles = provider.RoleGroups
				.Where(m => groups.Contains(m.Key) && StaffRoles.IsKnown(m.Value))
				.Select(m => m.Value)
				.Distinct(StringComparer.Ordinal)
				.ToList();
			bool bBanned = provider.BanGroup != null && groups.Contains(provider.BanGroup);

			Database.Upstream.Changes changes = await Database.Upstream.Apply(db, userID, provider.Source, roles, bBanned, $"Member of {provider.BanGroup} in {provider.DisplayName}");
			if (changes.Any)
			{
				s_log.LogInformation("{Provider} changed user {UserId}: roles +[{Added}] -[{Removed}], ban started {BanStarted}, ban ended {BanEnded}",
					provider.Name, userID, String.Join(",", changes.RolesAdded), String.Join(",", changes.RolesRemoved), changes.BanStarted, changes.BanEnded);
			}

			return changes;
		}

		// Display name rules: printable, trimmed, at most 32 characters, unique after normalization
		private static async Task<string> UniqueUsername(AppDbContext db, string? requested)
		{
			var builder = new StringBuilder();
			foreach (char c in (requested ?? String.Empty).Normalize(NormalizationForm.FormKC))
			{
				if (!Char.IsControl(c) && !Char.IsSurrogate(c) && c != '�')
				{
					builder.Append(Char.IsWhiteSpace(c) ? ' ' : c);
				}
			}

			string baseName = String.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
			if (baseName.Length == 0)
			{
				baseName = "player";
			}

			baseName = Truncate(baseName, User.UsernameMaxLength)!;

			for (int attempt = 1; attempt < 10000; attempt++)
			{
				string suffix = attempt == 1 ? String.Empty : "_" + attempt;
				string candidate = Truncate(baseName, User.UsernameMaxLength - suffix.Length) + suffix;
				string normalized = NameNormalizer.Normalize(candidate);
				if (normalized.Length <= User.UsernameMaxLength && !await db.Users.AnyAsync(u => u.UsernameNormalized == normalized))
				{
					return candidate;
				}
			}

			return "player_" + Guid.NewGuid().ToString("N").Substring(0, 12);
		}

		private static string? Truncate(string? value, int length) => value == null || value.Length <= length ? value : value.Substring(0, length);
	}
}
