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
	// SuggestedName: the provider's own name, cleaned, while the user still owes a chosen one; never stored (S17)
	public sealed record UpstreamSignInResult(User? User, string? Error, string? SuggestedName = null)
	{
		public static UpstreamSignInResult Refused(string error) => new(null, error);
	}

	// Who the provider says signed in: the subject identities store (null when the provider gave none we accept) and a name
	public sealed record UpstreamProfile(string? Subject, string? Name);

	// GitLab OmniAuth behaviour: a known identity signs in, an (opt-in) verified email finds the user, otherwise sign-up
	public sealed class UpstreamAccounts
	{
		public const string ErrorNoSubject = "no_subject";
		public const string ErrorSignUpDisabled = "sign_up_disabled";
		public const string ErrorBlocked = "blocked";
		public const string ErrorDeactivated = "deactivated";
		public const string ErrorIdentityConflict = "identity_conflict";

		private static readonly ILogger s_log = AppLog.For(typeof(UpstreamAccounts));
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public UpstreamAccounts(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
		}

		public async Task<UpstreamSignInResult> SignIn(UpstreamProviderSettings provider, ClaimsPrincipal external)
		{
			UpstreamProfile profile = ReadProfile(provider, external);
			string? subject = profile.Subject;
			if (String.IsNullOrEmpty(subject) || subject.Length > 128)
			{
				s_log.LogWarning("{Provider} sign-in carries no usable subject", provider.Name);
				return UpstreamSignInResult.Refused(ErrorNoSubject);
			}

			string? externName = Truncate(profile.Name, 128);

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
					return UpstreamSignInResult.Refused(ErrorIdentityConflict);
				}
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

				user = await FindByVerifiedEmail(db, provider, external);

				if (user == null)
				{
					// the provider id of an erased account under an active ban: refused as that account would be
					if (await IdentifierHashes.ProviderHeld(db, provider.Name, subject))
					{
						return UpstreamSignInResult.Refused(ErrorDeactivated);
					}
					if (!provider.AllowSignUp)
					{
						return UpstreamSignInResult.Refused(ErrorSignUpDisabled);
					}

					user = new User
					{
						Username = Database.DisplayNames.Placeholder(),
						State = UserStates.Active,
						// the player chooses their own name before the first token (S17)
						NameRequiredAt = now,
						CreatedAt = now,
						UpdatedAt = now
					};
					for (int attempt = 1; ; attempt++)
					{
						await Database.DisplayNames.AssignNewUser(db, user, attempt);
						db.Users.Add(user);
						try
						{
							await db.SaveChangesAsync();
							break;
						}
						catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex) && attempt < Database.DisplayNames.NewUserAttempts)
						{
							// a sign-up took the same placeholder and tag at the same moment; EF rolled back to its savepoint
							db.Entry(user).State = EntityState.Detached;
						}
					}
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

			identity.LastUsedAt = now;
			user.LastSignInAt = now;
			await db.SaveChangesAsync();

			return new UpstreamSignInResult(user, null, user.NameRequiredAt != null ? Suggestion(externName) : null);
		}

		private const string SteamClaimedIDPrefix = "https://steamcommunity.com/openid/id/";

		// The subject in the form the import stores it: SteamID64 for Steam, the member id for GameReplays, the provider's
		// user id (sub or the name identifier OpenIddict maps it to) for the others
		public static UpstreamProfile ReadProfile(UpstreamProviderSettings provider, ClaimsPrincipal external)
		{
			string? Claim(string type) => external.FindFirst(type)?.Value is { Length: > 0 } value ? value : null;
			string? name = Claim(OpenIddictConstants.Claims.PreferredUsername) ?? Claim(OpenIddictConstants.Claims.Name) ?? Claim(ClaimTypes.Name);

			switch (provider.Kind)
			{
				case ProviderKind.Steam:
				{
					// the verified OpenID 2.0 claimed id; only Steam's own https form is a Steam account
					string? claimed = Claim(ClaimTypes.NameIdentifier);
					string? steamID = claimed != null && claimed.StartsWith(SteamClaimedIDPrefix, StringComparison.Ordinal) ? claimed.Substring(SteamClaimedIDPrefix.Length) : null;
					bool valid = steamID != null && steamID.Length is > 0 and <= 20 && steamID.All(Char.IsAsciiDigit);
					return new UpstreamProfile(valid ? steamID : null, name);
				}
				case ProviderKind.GameReplays:
				{
					// resource.php answers the member itself or wraps it in "user"; the name is members_display_name only,
					// GameReplays: "You can ignore username."
					string? id = Claim("id");
					string? memberName = Claim("members_display_name");
					if (id == null && Claim("user") is string wrapped)
					{
						try
						{
							using var document = System.Text.Json.JsonDocument.Parse(wrapped);
							System.Text.Json.JsonElement user = document.RootElement;
							id = user.TryGetProperty("id", out var idValue) ? idValue.ToString() : null;
							memberName ??= user.TryGetProperty("members_display_name", out var display) && display.ValueKind == System.Text.Json.JsonValueKind.String ? display.GetString() : null;
						}
						catch (System.Text.Json.JsonException)
						{
							id = null;
						}
					}
					return new UpstreamProfile(String.IsNullOrEmpty(id) ? null : id, memberName);
				}
				default:
					return new UpstreamProfile(Claim(OpenIddictConstants.Claims.Subject) ?? Claim(ClaimTypes.NameIdentifier), name);
			}
		}

		// An unknown identity links by a verified email only where the operator allows it
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

		// The provider's name as a suggestion for the choose-a-name box: printable, trimmed, at most 32 characters; null when
		// nothing usable is left. The box checks the name rules when the player submits it.
		private static string? Suggestion(string? requested)
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

			return name.Length == 0 ? null : name;
		}

		private static string? Truncate(string? value, int length) => value == null || value.Length <= length ? value : value.Substring(0, length);
	}
}
