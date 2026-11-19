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

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GenOnlineService.Identity
{
	// ASP.NET Core Identity over the users, identities and user_recovery_codes tables: sign-in providers, email +
	// password accounts with lockout, and TOTP with recovery codes
	public sealed class GoUserStore : IUserStore<User>, IUserLoginStore<User>, IUserSecurityStampStore<User>, IUserPasswordStore<User>,
		IUserEmailStore<User>, IUserLockoutStore<User>, IUserTwoFactorStore<User>, IUserAuthenticatorKeyStore<User>, IUserTwoFactorRecoveryCodeStore<User>
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;
		private readonly IDataProtector m_authenticatorKeys;

		public GoUserStore(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection)
		{
			m_dbFactory = dbFactory;
			m_authenticatorKeys = dataProtection.CreateProtector("GenOnline.Identity.AuthenticatorKey");
		}

		public void Dispose()
		{
		}

		public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.ID.ToString());

		public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Username);

		public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken)
		{
			user.Username = userName ?? String.Empty;
			return Task.CompletedTask;
		}

		public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.UsernameNormalized);

		// Identity's own normalizer is ignored: the schema key is NFKC upper invariant
		public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken)
		{
			user.UsernameNormalized = NameNormalizer.Normalize(user.Username);
			return Task.CompletedTask;
		}

		public async Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken)
		{
			// a deadlocked attempt (InnoDB) is rolled back by the database; the retry starts with a clean user row
			const int DeadlockAttempts = 3;
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					return await CreateOnce(user, cancellationToken);
				}
				catch (Exception ex) when (DbHelpers.IsDeadlock(ex) && attempt < DeadlockAttempts)
				{
					user.ID = 0;
				}
			}
		}

		private async Task<IdentityResult> CreateOnce(User user, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			user.CreatedAt = user.UpdatedAt = DateTime.UtcNow;

			// the address is the login identifier, with no mailbox logic: the account commits only if no other account uses
			// this exact address and no ban holds it. The checks and the account row share one transaction, so a refused
			// sign-up leaves nothing behind.
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
			if (await Database.Users.AddressHeldByOther(db, user.EmailNormalized, user.ID, cancellationToken))
			{
				await transaction.RollbackAsync(cancellationToken);
				return DuplicateAddress();
			}
			for (int attempt = 1; ; attempt++)
			{
				await Database.DisplayNames.AssignNewUser(db, user, attempt);
				db.Users.Add(user);
				try
				{
					await db.SaveChangesAsync(cancellationToken);
					break;
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
				{
					// EF rolled back to its savepoint. A concurrent sign-up may have taken this exact address (the unique index),
					// which is not a name collision; otherwise a look-alike took the same tag, so pick another. The check runs on
					// its own connection: on MySQL the transaction's snapshot predates the other sign-up's commit.
					db.Entry(user).State = EntityState.Detached;
					await using AppDbContext fresh = await m_dbFactory.CreateDbContextAsync(cancellationToken);
					if (await Database.Users.AddressHeldByOther(fresh, user.EmailNormalized, user.ID, cancellationToken))
					{
						await transaction.RollbackAsync(cancellationToken);
						return DuplicateAddress();
					}
					if (attempt < Database.DisplayNames.NewUserAttempts)
					{
						continue;
					}
					await transaction.RollbackAsync(cancellationToken);
					return IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = "The username is taken." });
				}
			}

			if (await IdentifierHashes.EmailHeldByBan(db, user.Email, user.ID, DateTime.UtcNow, cancellationToken))
			{
				await transaction.RollbackAsync(cancellationToken);
				return BannedAddress();
			}
			await transaction.CommitAsync(cancellationToken);
			return IdentityResult.Success;
		}

		private static IdentityResult BannedAddress() =>
			IdentityResult.Failed(new IdentityError { Code = GoUserValidator.DuplicateEmail, Description = "This email address cannot be used while a ban on it is active." });

		private static IdentityResult DuplicateAddress() =>
			IdentityResult.Failed(new IdentityError { Code = GoUserValidator.DuplicateEmail, Description = "This email address is already used by another account." });

		public async Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			string? storedEmail = await db.Users.AsNoTracking().Where(u => u.ID == user.ID).Select(u => u.EmailNormalized).FirstOrDefaultAsync(cancellationToken);
			user.UpdatedAt = DateTime.UtcNow;
			var entry = db.Users.Update(user);
			// Identity writes the copy it read; the state (blocks), the name, tag and owed name (renames) belong to other
			// code and may have changed meanwhile, so a failed sign-in never writes back a stale copy of them
			entry.Property(u => u.State).IsModified = false;
			entry.Property(u => u.Username).IsModified = false;
			entry.Property(u => u.UsernameNormalized).IsModified = false;
			entry.Property(u => u.NameTag).IsModified = false;
			entry.Property(u => u.NameKey).IsModified = false;
			entry.Property(u => u.RenamedAt).IsModified = false;
			entry.Property(u => u.NameRequiredAt).IsModified = false;
			if (String.Equals(storedEmail, user.EmailNormalized, StringComparison.Ordinal))
			{
				await db.SaveChangesAsync(cancellationToken);
				return IdentityResult.Success;
			}

			// a new address commits only if no other account uses it and no ban holds it, as at sign-up
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
			if (await Database.Users.AddressHeldByOther(db, user.EmailNormalized, user.ID, cancellationToken))
			{
				await transaction.RollbackAsync(cancellationToken);
				return DuplicateAddress();
			}
			try
			{
				await db.SaveChangesAsync(cancellationToken);
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				await transaction.RollbackAsync(cancellationToken);
				return DuplicateAddress();
			}
			db.ChangeTracker.Clear();
			if (await IdentifierHashes.EmailHeldByBan(db, user.Email, user.ID, DateTime.UtcNow, cancellationToken))
			{
				await transaction.RollbackAsync(cancellationToken);
				return BannedAddress();
			}
			await transaction.CommitAsync(cancellationToken);
			return IdentityResult.Success;
		}

		// Deleting an account erases its personal data and keeps the row as a tombstone, so ids, matches and bans stay intact
		// (AccountErasure); the owner is the actor
		public async Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			var actor = new Database.ModerationActor { Kind = AuditActorKinds.User, UserID = user.ID, Name = $"user:{user.ID}", Source = ModerationSources.Api };
			ErasureResult result = await AccountErasure.Erase(db, user.ID, actor, null, live: true, cancellationToken);
			return result.Found
				? IdentityResult.Success
				: IdentityResult.Failed(new IdentityError { Code = "NotFound", Description = "The account does not exist." });
		}

		public async Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken)
		{
			if (!Int64.TryParse(userId, out Int64 id))
			{
				return null;
			}

			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ID == id, cancellationToken);
		}

		public async Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			// Names are not unique: "Name#0042" finds one user, a bare name only when exactly one user has it
			int hash = normalizedUserName.LastIndexOf('#');
			if (hash > 0 && Int32.TryParse(normalizedUserName.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int tag))
			{
				string name = NameNormalizer.Normalize(normalizedUserName.Substring(0, hash));
				return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UsernameNormalized == name && u.NameTag == tag, cancellationToken);
			}

			string normalized = NameNormalizer.Normalize(normalizedUserName);
			List<User> matches = await db.Users.AsNoTracking().Where(u => u.UsernameNormalized == normalized).Take(2).ToListAsync(cancellationToken);
			return matches.Count == 1 ? matches[0] : null;
		}

		public async Task AddLoginAsync(User user, UserLoginInfo login, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			db.Identities.Add(new UserIdentity
			{
				UserID = user.ID,
				Provider = login.LoginProvider,
				ExternUid = login.ProviderKey,
				CreatedAt = DateTime.UtcNow,
				LastUsedAt = DateTime.UtcNow
			});
			await db.SaveChangesAsync(cancellationToken);
		}

		public async Task RemoveLoginAsync(User user, string loginProvider, string providerKey, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			await db.Identities.Where(i => i.UserID == user.ID && i.Provider == loginProvider && i.ExternUid == providerKey).ExecuteDeleteAsync(cancellationToken);
		}

		public async Task<IList<UserLoginInfo>> GetLoginsAsync(User user, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			return await db.Identities.AsNoTracking().Where(i => i.UserID == user.ID)
				.Select(i => new UserLoginInfo(i.Provider, i.ExternUid, null))
				.ToListAsync(cancellationToken);
		}

		public async Task<User?> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			Int64? userID = await db.Identities.AsNoTracking().Where(i => i.Provider == loginProvider && i.ExternUid == providerKey).Select(i => (Int64?)i.UserID).FirstOrDefaultAsync(cancellationToken);
			return userID == null ? null : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ID == userID, cancellationToken);
		}

		public Task SetSecurityStampAsync(User user, string stamp, CancellationToken cancellationToken)
		{
			user.SecurityStamp = stamp;
			return Task.CompletedTask;
		}

		public Task<string?> GetSecurityStampAsync(User user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.SecurityStamp);

		// Passwords: the hash only, null without a local password

		public Task SetPasswordHashAsync(User user, string? passwordHash, CancellationToken cancellationToken)
		{
			user.PasswordHash = passwordHash;
			return Task.CompletedTask;
		}

		public Task<string?> GetPasswordHashAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);

		public Task<bool> HasPasswordAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash != null);

		// Email: unique by its NFKC upper invariant key, as names are; Identity's own normalizer is ignored

		public Task SetEmailAsync(User user, string? email, CancellationToken cancellationToken)
		{
			user.Email = email;
			user.EmailNormalized = email == null ? null : NameNormalizer.Normalize(email);
			return Task.CompletedTask;
		}

		public Task<string?> GetEmailAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.Email);

		public Task<bool> GetEmailConfirmedAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.EmailConfirmedAt != null);

		public Task SetEmailConfirmedAsync(User user, bool confirmed, CancellationToken cancellationToken)
		{
			user.EmailConfirmedAt = confirmed ? user.EmailConfirmedAt ?? DateTime.UtcNow : null;
			return Task.CompletedTask;
		}

		public async Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
		{
			string normalized = NameNormalizer.Normalize(normalizedEmail);
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.EmailNormalized == normalized, cancellationToken);
		}

		public Task<string?> GetNormalizedEmailAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.EmailNormalized);

		public Task SetNormalizedEmailAsync(User user, string? normalizedEmail, CancellationToken cancellationToken)
		{
			user.EmailNormalized = user.Email == null ? null : NameNormalizer.Normalize(user.Email);
			return Task.CompletedTask;
		}

		// Lockout: every account can be locked; the end is stored in UTC

		public Task<DateTimeOffset?> GetLockoutEndDateAsync(User user, CancellationToken cancellationToken)
			=> Task.FromResult(user.LockoutEndAt == null ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(user.LockoutEndAt.Value, DateTimeKind.Utc)));

		public Task SetLockoutEndDateAsync(User user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
		{
			user.LockoutEndAt = lockoutEnd?.UtcDateTime;
			return Task.CompletedTask;
		}

		public Task<int> IncrementAccessFailedCountAsync(User user, CancellationToken cancellationToken)
		{
			user.FailedSignInCount++;
			return Task.FromResult(user.FailedSignInCount);
		}

		public Task ResetAccessFailedCountAsync(User user, CancellationToken cancellationToken)
		{
			user.FailedSignInCount = 0;
			return Task.CompletedTask;
		}

		public Task<int> GetAccessFailedCountAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.FailedSignInCount);

		public Task<bool> GetLockoutEnabledAsync(User user, CancellationToken cancellationToken) => Task.FromResult(true);

		public Task SetLockoutEnabledAsync(User user, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

		// Two factor

		public Task SetTwoFactorEnabledAsync(User user, bool enabled, CancellationToken cancellationToken)
		{
			user.TwoFactorEnabled = enabled;
			return Task.CompletedTask;
		}

		public Task<bool> GetTwoFactorEnabledAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.TwoFactorEnabled);

		public Task SetAuthenticatorKeyAsync(User user, string key, CancellationToken cancellationToken)
		{
			user.AuthenticatorKey = m_authenticatorKeys.Protect(key);
			return Task.CompletedTask;
		}

		public Task<string?> GetAuthenticatorKeyAsync(User user, CancellationToken cancellationToken)
			=> Task.FromResult(user.AuthenticatorKey == null ? null : m_authenticatorKeys.Unprotect(user.AuthenticatorKey));

		// Recovery codes: only their SHA-256 is stored (each is a random one-time secret), new codes replace the old

		private static string HashCode(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));

		public async Task ReplaceCodesAsync(User user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
			await db.UserRecoveryCodes.Where(c => c.UserID == user.ID).ExecuteDeleteAsync(cancellationToken);
			DateTime now = DateTime.UtcNow;
			db.UserRecoveryCodes.AddRange(recoveryCodes.Select(code => new UserRecoveryCode { UserID = user.ID, CodeHash = HashCode(code), CreatedAt = now }));
			await db.SaveChangesAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);
		}

		// A code is used at most once, also when two sign-ins race with the same code
		public async Task<bool> RedeemCodeAsync(User user, string code, CancellationToken cancellationToken)
		{
			string hash = HashCode(code);
			DateTime now = DateTime.UtcNow;
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			int used = await db.UserRecoveryCodes.Where(c => c.UserID == user.ID && c.CodeHash == hash && c.UsedAt == null)
				.ExecuteUpdateAsync(set => set.SetProperty(c => c.UsedAt, now), cancellationToken);
			return used > 0;
		}

		public async Task<int> CountCodesAsync(User user, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			return await db.UserRecoveryCodes.CountAsync(c => c.UserID == user.ID && c.UsedAt == null, cancellationToken);
		}
	}
}
