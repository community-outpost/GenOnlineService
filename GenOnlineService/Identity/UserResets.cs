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
using System.Security.Cryptography;
using System.Text.Json;

namespace GenOnlineService.Identity
{
	// The operator's way back into an email + password account without mail: a new password, or the end of two-factor
	// sign-in. Each ends the lockout, rotates the security stamp (website sessions stop within a minute) and is audited.
	public static class UserResets
	{
		public const string PasswordResetAction = "password_reset";
		public const string TwoFactorResetAction = "two_factor_reset";

		// no look-alikes (0/O, 1/l/I), so the password can be read out to its owner
		private const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
		private const int GeneratedLength = 20;

		// Sets the given password, or a new random one, and returns it
		public static async Task<string> ResetPassword(AppDbContext db, Int64 userID, string? password, int minLength, string actor, string? reason)
		{
			User user = await Find(db, userID);
			if (user.Email == null)
			{
				throw new InvalidOperationException($"User {userID} has no email address to sign in with a password; it signs in with a provider only.");
			}

			string chosen = password ?? RandomNumberGenerator.GetString(PasswordAlphabet, GeneratedLength);
			if (chosen.Length < minLength || chosen.Length > PasswordSignInSettings.MaximumLength)
			{
				throw new InvalidOperationException($"Passwords must be at least {minLength} characters and at most {PasswordSignInSettings.MaximumLength}.");
			}

			user.PasswordHash = new PasswordHasher<User>().HashPassword(user, chosen);
			Unlock(user);
			db.AuditLog.Add(Audit(PasswordResetAction, userID, actor, reason));
			await db.SaveChangesAsync();
			return chosen;
		}

		public static async Task ResetTwoFactor(AppDbContext db, Int64 userID, string actor, string? reason)
		{
			User user = await Find(db, userID);
			await using var transaction = await db.Database.BeginTransactionAsync();
			user.TwoFactorEnabled = false;
			user.AuthenticatorKey = null;
			Unlock(user);
			db.AuditLog.Add(Audit(TwoFactorResetAction, userID, actor, reason));
			await db.SaveChangesAsync();
			await db.UserRecoveryCodes.Where(c => c.UserID == userID).ExecuteDeleteAsync();
			await transaction.CommitAsync();
		}

		private static async Task<User> Find(AppDbContext db, Int64 userID)
			=> await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == userID) ?? throw new InvalidOperationException($"No user with id {userID}.");

		private static void Unlock(User user)
		{
			user.LockoutEndAt = null;
			user.FailedSignInCount = 0;
			user.SecurityStamp = Guid.NewGuid().ToString("N");
			user.UpdatedAt = DateTime.UtcNow;
		}

		private static AuditLogEntry Audit(string action, Int64 userID, string actor, string? reason) => new()
		{
			CreatedAt = DateTime.UtcNow,
			ActorKind = AuditActorKinds.Cli,
			Actor = actor.Length > 64 ? actor.Substring(0, 64) : actor,
			Action = action,
			TargetKind = AuditTargetKinds.User,
			TargetID = userID,
			Reason = String.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
			Source = "cli",
			Details = JsonSerializer.Serialize(new { })
		};
	}
}
