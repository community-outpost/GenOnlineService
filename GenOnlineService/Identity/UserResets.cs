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
	// sign-in. Each ends the lockout, rotates the security stamp (website sessions stop within a minute), revokes every
	// sign-in of the account with its refresh tokens and game sessions and every personal token it owns, and is audited.
	public static class UserResets
	{
		public const string PasswordResetAction = "password_reset";
		public const string TwoFactorResetAction = "two_factor_reset";

		// no look-alikes (0/O, 1/l/I), so the password can be read out to its owner
		private const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
		private const int GeneratedLength = 20;

		// Sets the given password, or a new random one, and returns it
		public static Task<string> ResetPassword(AppDbContext db, Int64 userID, string? password, int minLength, string actor, string? reason)
			=> ResetPassword(db, userID, password, minLength, CliActor(actor), reason, live: false);

		public static Task ResetTwoFactor(AppDbContext db, Int64 userID, string actor, string? reason)
			=> ResetTwoFactor(db, userID, CliActor(actor), reason, live: false);

		// The CLI and the staff pages share these; the actor gives the audit row its kind, id, name and source. live is
		// the running service, which also drops the game sessions it holds and closes the realtime connections at once
		public static async Task<string> ResetPassword(AppDbContext db, Int64 userID, string? password, int minLength, Database.ModerationActor actor, string? reason, bool live)
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

			await using var transaction = await db.Database.BeginTransactionAsync();
			user.PasswordHash = new PasswordHasher<User>().HashPassword(user, chosen);
			Unlock(user);
			(int signIns, int tokens) = await RevokeSignIns(db, userID, actor, PasswordResetAction);
			db.AuditLog.Add(Audit(PasswordResetAction, userID, actor, reason, signIns, tokens));
			await db.SaveChangesAsync();
			await transaction.CommitAsync();
			await EndLive(db, userID, live, tokens, "password reset");
			return chosen;
		}

		public static async Task ResetTwoFactor(AppDbContext db, Int64 userID, Database.ModerationActor actor, string? reason, bool live)
		{
			User user = await Find(db, userID);
			await using var transaction = await db.Database.BeginTransactionAsync();
			user.TwoFactorEnabled = false;
			user.AuthenticatorKey = null;
			Unlock(user);
			(int signIns, int tokens) = await RevokeSignIns(db, userID, actor, TwoFactorResetAction);
			db.AuditLog.Add(Audit(TwoFactorResetAction, userID, actor, reason, signIns, tokens));
			await db.SaveChangesAsync();
			await db.UserRecoveryCodes.Where(c => c.UserID == userID).ExecuteDeleteAsync();
			await transaction.CommitAsync();
			await EndLive(db, userID, live, tokens, "two-factor reset");
		}

		// Every stored sign-in and its tokens end, the stored game sessions go and every personal gopat- token of the
		// account is revoked (each with its token_revoke row), so no copy of the service and no refresh accepts them again;
		// returns the number of sign-ins and of personal tokens revoked
		private static async Task<(int SignIns, int Tokens)> RevokeSignIns(AppDbContext db, Int64 userID, Database.ModerationActor actor, string action)
		{
			int signIns = await SignInRevocation.RevokeStored(db, userID);
			await Database.GameSessionStore.DeleteUser(db, userID, null);

			DateTime now = DateTime.UtcNow;
			List<ApiToken> owned = await db.ApiTokens.AsTracking()
				.Where(t => t.OwnerUserID == userID && (t.RevokedAt == null || t.RevokedAt > now) && t.ExpiresAt > now)
				.ToListAsync();
			var tokenActor = new Rest.ApiTokenActor(actor.Kind, actor.UserID, actor.Name, actor.Source);
			int tokens = 0;
			foreach (ApiToken token in owned)
			{
				if (await Rest.ApiTokenAdmin.Revoke(db, tokenActor, token, action))
				{
					tokens++;
				}
			}
			return (signIns, tokens);
		}

		// The token cache of this process follows at once, other copies on their reconcile tick; the running service also
		// forgets the game sessions it caches and closes the realtime connections with auth.revoked
		private static async Task EndLive(AppDbContext db, Int64 userID, bool live, int tokens, string reason)
		{
			if (tokens > 0)
			{
				await ApiTokenCache.Reload(db);
			}
			if (live)
			{
				await SessionRevocation.RevokeUser(userID, false, reason);
			}
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

		private static Database.ModerationActor CliActor(string actor) => new() { Kind = AuditActorKinds.Cli, Name = actor, Source = ModerationSources.Cli };

		private static AuditLogEntry Audit(string action, Int64 userID, Database.ModerationActor actor, string? reason, int signInsRevoked, int tokensRevoked) => new()
		{
			CreatedAt = DateTime.UtcNow,
			ActorKind = actor.Kind,
			ActorID = actor.UserID,
			Actor = actor.Name.Length > 64 ? actor.Name.Substring(0, 64) : actor.Name,
			Action = action,
			TargetKind = AuditTargetKinds.User,
			TargetID = userID,
			Reason = String.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
			Source = actor.Source,
			Details = JsonSerializer.Serialize(new { sign_ins_revoked = signInsRevoked, tokens_revoked = tokensRevoked })
		};
	}
}
