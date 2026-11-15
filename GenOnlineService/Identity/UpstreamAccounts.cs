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

	// GitLab OmniAuth behaviour: a known identity signs in, a player id or (opt-in) verified email finds the user, otherwise sign-up
	public sealed class UpstreamAccounts
	{
		public const string ErrorNoSubject = "no_subject";
		public const string ErrorSignUpDisabled = "sign_up_disabled";
		public const string ErrorBlocked = "blocked";
		public const string ErrorDeactivated = "deactivated";
		public const string ErrorNoPlayerID = "no_player_id";
		public const string ErrorPlayerIDMismatch = "player_id_mismatch";

		// The display name and its tag, copied from a trusted provider at every sign-in (identity section 5a)
		public const string NameClaim = "name";
		public const string NameTagClaim = "name_tag";

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
				try
				{
					return await SignInOnce(provider, external, subject, externName);
				}
				catch (DbUpdateException again) when (DbHelpers.IsUniqueViolation(again))
				{
					// another upstream account linked the same user in between: one identity per provider per user
					s_log.LogWarning("{Provider} sign-in of {Subject} lost a race for its user", provider.Name, subject);
					return UpstreamSignInResult.Refused(ErrorPlayerIDMismatch);
				}
			}
		}

		private async Task<UpstreamSignInResult> SignInOnce(UpstreamProviderSettings provider, ClaimsPrincipal external, string subject, string? externName)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync();
			DateTime now = DateTime.UtcNow;

			// with player ids the upstream claim is the user id: no sign-in without one, and an identity never moves to another id
			Int64? playerID = null;
			if (provider.PlayerIDs)
			{
				if (!Int64.TryParse(external.GetClaim(provider.PlayerIDClaim), NumberStyles.None, CultureInfo.InvariantCulture, out Int64 claimed) || claimed <= 0)
				{
					s_log.LogWarning("{Provider} sign-in of {Subject} carries no numeric {Claim}", provider.Name, subject, provider.PlayerIDClaim);
					return UpstreamSignInResult.Refused(ErrorNoPlayerID);
				}

				playerID = claimed;
			}

			UserIdentity? identity = await db.Identities.AsTracking().FirstOrDefaultAsync(i => i.Provider == provider.Name && i.ExternUid == subject);
			if (identity != null && playerID != null && identity.UserID != playerID)
			{
				s_log.LogWarning("{Provider} identity {Subject} belongs to user {UserId} but asserts {Claim} {PlayerId}", provider.Name, subject, identity.UserID, provider.PlayerIDClaim, playerID);
				return UpstreamSignInResult.Refused(ErrorPlayerIDMismatch);
			}

			User? user = identity == null ? null : await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == identity.UserID);

			if (user == null)
			{
				// a new user and its identity commit together, so a failed link never leaves an orphan account
				await using var tx = await db.Database.BeginTransactionAsync();

				if (playerID != null)
				{
					// an imported or earlier user with this id is the same person, unless another identity of this provider already holds it
					user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == playerID);
					if (user != null && await db.Identities.AnyAsync(i => i.UserID == playerID && i.Provider == provider.Name))
					{
						s_log.LogWarning("{Provider} identity {Subject} asserts {Claim} {PlayerId}, which another identity of the provider holds", provider.Name, subject, provider.PlayerIDClaim, playerID);
						return UpstreamSignInResult.Refused(ErrorPlayerIDMismatch);
					}
				}
				else
				{
					user = await FindByVerifiedEmail(db, provider, external);
				}

				if (user == null)
				{
					if (!provider.AllowSignUp)
					{
						return UpstreamSignInResult.Refused(ErrorSignUpDisabled);
					}

					// with player ids the id is set explicitly, so the database never picks one Keycloak gives out later
					user = new User
					{
						Username = InitialName(provider.Trusted ? external.GetClaim(NameClaim) ?? externName : externName),
						State = UserStates.Active,
						CreatedAt = now,
						UpdatedAt = now
					};
					if (playerID != null)
					{
						user.ID = playerID.Value;
					}
					// a trusted provider's name and tag are taken as they are, so no throwaway tag reaches the name history
					if (!provider.Trusted || !await Database.DisplayNames.TryAssignUpstream(db, user, external.GetClaim(NameClaim), external.GetClaim(NameTagClaim)))
					{
						await Database.DisplayNames.AssignNewUser(db, user);
					}
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
				foreach (NameChange change in await ApplyUpstreamName(db, provider, user.ID, external.GetClaim(NameClaim), external.GetClaim(NameTagClaim)))
				{
					if (change.UserID == user.ID)
					{
						user.Username = change.NewName;
						user.UsernameNormalized = NameNormalizer.Normalize(change.NewName);
						user.NameKey = GenOnlineService.Names.NameKey.Of(change.NewName);
						user.NameTag = change.NewTag;
					}
				}
				await ApplyTrustedClaims(db, provider, user.ID, GroupsOf(provider, external));
			}

			return new UpstreamSignInResult(user, null);
		}

		// Copies a trusted provider's name and tag (the provider owns them) and tells live sessions about any change
		public static async Task<IReadOnlyList<NameChange>> ApplyUpstreamName(AppDbContext db, UpstreamProviderSettings provider, Int64 userID, string? name, string? tag)
		{
			if (name == null && tag == null)
			{
				return Array.Empty<NameChange>();
			}

			IReadOnlyList<NameChange> changes = await Database.DisplayNames.ApplyUpstream(db, userID, name, tag, provider.Source);
			Realtime.RealtimeNames.Publish(changes);
			return changes;
		}

		// Without player ids an unknown identity links by a verified email only where the operator allows it
		private static async Task<User?> FindByVerifiedEmail(AppDbContext db, UpstreamProviderSettings provider, ClaimsPrincipal external)
		{
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

		// A first name for a new account: printable, trimmed, at most 32 characters; the tag makes it unique
		private static string InitialName(string? requested)
		{
			var builder = new StringBuilder();
			foreach (char c in (requested ?? String.Empty).Normalize(NormalizationForm.FormKC))
			{
				if (!Char.IsControl(c) && !Char.IsSurrogate(c) && c != '�' && c != '#')
				{
					builder.Append(Char.IsWhiteSpace(c) ? ' ' : c);
				}
			}

			string name = String.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
			name = Truncate(name, User.UsernameMaxLength)!;
			while (name.Length > 0 && NameNormalizer.Normalize(name).Length > User.UsernameMaxLength)
			{
				name = name.Substring(0, name.Length - 1).TrimEnd();
			}

			return name.Length == 0 ? "player" : name;
		}

		private static string? Truncate(string? value, int length) => value == null || value.Length <= length ? value : value.Substring(0, length);
	}
}
