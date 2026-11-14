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
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GenOnlineService.Identity
{
	// ASP.NET Core Identity over the schema v1 users and identities tables; local passwords and 2FA come in phase 3
	public sealed class GoUserStore : IUserStore<User>, IUserLoginStore<User>, IUserSecurityStampStore<User>
	{
		private readonly IDbContextFactory<AppDbContext> m_dbFactory;

		public GoUserStore(IDbContextFactory<AppDbContext> dbFactory)
		{
			m_dbFactory = dbFactory;
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
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			user.CreatedAt = user.UpdatedAt = DateTime.UtcNow;

			for (int attempt = 1; ; attempt++)
			{
				await Database.DisplayNames.AssignNewUser(db, user);
				db.Users.Add(user);
				try
				{
					await db.SaveChangesAsync(cancellationToken);
					return IdentityResult.Success;
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex) && attempt < 3)
				{
					// a look-alike sign-up took the same tag at the same moment, so pick another
					db.Entry(user).State = EntityState.Detached;
				}
				catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
				{
					return IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = "The username is taken." });
				}
			}
		}

		public async Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken)
		{
			await using AppDbContext db = await m_dbFactory.CreateDbContextAsync(cancellationToken);
			user.UpdatedAt = DateTime.UtcNow;
			db.Users.Update(user);
			await db.SaveChangesAsync(cancellationToken);
			return IdentityResult.Success;
		}

		// Accounts are deactivated, never deleted, so ids and match history stay intact
		public Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken)
		{
			return Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "NotSupported", Description = "Accounts are deactivated, not deleted." }));
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
				ExternUsername = login.ProviderDisplayName,
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
				.Select(i => new UserLoginInfo(i.Provider, i.ExternUid, i.ExternUsername))
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
	}
}
