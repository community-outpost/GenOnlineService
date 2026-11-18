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

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenOnlineService.Identity
{
	public sealed record ErasureResult(bool Found, bool AlreadyErased, bool BanHeld, int MatchesRenamed);

	// An account whose personal data was erased; its users row stays as the tombstone. A table of its own, not a users column,
	// so the users table keeps its shape
	public class ErasedAccount
	{
		public Int64 UserID { get; set; }
		public DateTime ErasedAt { get; set; } = DateTime.UnixEpoch;
	}

	public class ErasedAccountConfiguration : IEntityTypeConfiguration<ErasedAccount>
	{
		public void Configure(EntityTypeBuilder<ErasedAccount> builder)
		{
			builder.ToTable("erased_accounts");
			builder.HasKey(e => e.UserID);
			builder.Property(e => e.UserID).HasColumnName("user_id").ValueGeneratedNever();
			builder.Property(e => e.ErasedAt).HasColumnName("erased_at");
		}
	}

	// Erasure of an account on request (Privacy Policy, "Your rights"). The users row stays as a tombstone under the same id,
	// so matches, ratings, stats, leaderboards and sanctions keep pointing at it; every personal field is cleared and the name
	// becomes a neutral one. The player's name leaves their matches (replay files are not touched). While a ban is active, the
	// device ids and the sign-in provider links stay so the same machine or Steam/Discord account cannot sign up again
	// unbanned; PurgeHeldIdentities removes the links once no ban is active, the device purge the devices 90 days later.
	// Moderation records stay for their own retention: sanctions, their evidence, the staff log, reports, anti-cheat reports
	// and automated rule matches.
	public static class AccountErasure
	{
		public const string Action = "account_erase";

		// "Deleted" and six digits: never a name a player owes or chose, and it spreads over many names so tags never run out
		public static string TombstoneName() => "Deleted" + Random.Shared.Next(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

		public static async Task<ErasureResult> Erase(AppDbContext db, Int64 userID, Database.ModerationActor actor, string? reason, bool live, CancellationToken cancellationToken = default)
		{
			User? user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.ID == userID, cancellationToken);
			if (user == null)
			{
				return new ErasureResult(false, false, false, 0);
			}
			if (await db.ErasedAccounts.AnyAsync(e => e.UserID == userID, cancellationToken))
			{
				return new ErasureResult(true, true, false, 0);
			}

			DateTime now = DateTime.UtcNow;
			bool banned = await Database.Moderation.ActiveSanctions(db, SanctionTypes.Ban, now).AnyAsync(s => s.UserID == userID, cancellationToken);

			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

			// sign-ins, game sessions and personal tokens end first, as a password reset ends them
			(int signIns, int tokens) = await UserResets.RevokeSignIns(db, userID, actor, Action);

			string name = TombstoneName();
			user.Username = name;
			user.UsernameNormalized = NameNormalizer.Normalize(name);
			user.NameTag = await Database.DisplayNames.FreeTag(db, name, userID);
			user.Email = null;
			user.EmailNormalized = null;
			user.EmailConfirmedAt = null;
			user.PasswordHash = null;
			user.AuthenticatorKey = null;
			user.TwoFactorEnabled = false;
			user.SecurityStamp = Guid.NewGuid().ToString("N");
			user.LockoutEndAt = null;
			user.FailedSignInCount = 0;
			user.LastSignInAt = null;
			user.RenamedAt = null;
			user.NameRequiredAt = null;
			user.State = UserStates.Deactivated;
			user.UpdatedAt = now;
			db.ErasedAccounts.Add(new ErasedAccount { UserID = userID, ErasedAt = now });
			await db.SaveChangesAsync(cancellationToken);
			db.ChangeTracker.Clear();

			await db.UserUsernameHistory.Where(h => h.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			await db.NameReservations.Where(r => r.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			await db.UserRecoveryCodes.Where(c => c.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			await db.UserAgreements.Where(a => a.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			await db.UserRoles.Where(r => r.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			await db.Friendships.Where(f => f.UserIdLow == userID || f.UserIdHigh == userID).ExecuteDeleteAsync(cancellationToken);
			await db.FriendRequests.Where(f => f.FromUserId == userID || f.ToUserId == userID).ExecuteDeleteAsync(cancellationToken);
			await db.Blocks.Where(b => b.UserId == userID || b.BlockedUserId == userID).ExecuteDeleteAsync(cancellationToken);
			await db.PlayerPreferences.Where(p => p.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			await db.PlayerNetwork.Where(p => p.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			await db.SessionEvents.Where(e => e.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			await Database.Chat.DeleteUserData(db, userID, cancellationToken);

			if (!banned)
			{
				await db.Identities.Where(i => i.UserID == userID).ExecuteDeleteAsync(cancellationToken);
				await db.Devices.Where(d => d.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			}

			int matches = await RemoveNameFromMatches(db, userID, cancellationToken);

			// no name in the row: the staff log outlives the erasure
			db.AuditLog.Add(Database.Moderation.Entry(actor, Action, AuditTargetKinds.User, userID, reason,
				new { sign_ins = signIns, tokens, matches, ban_held = banned ? 1 : 0 }, now));
			await db.SaveChangesAsync(cancellationToken);
			await transaction.CommitAsync(cancellationToken);

			await UserResets.EndLive(db, userID, live, tokens, "account erased");
			if (live)
			{
				await AccessControl.RevokeInactiveUser(db, userID, UserStates.Deactivated);
				await StaffRoles.Reload(db);
			}

			return new ErasureResult(true, false, banned, matches);
		}

		// Blanks display_name in the player's match rows; the rest of each row's details stays as it was
		private static async Task<int> RemoveNameFromMatches(AppDbContext db, Int64 userID, CancellationToken cancellationToken)
		{
			var rows = await db.MatchPlayers.AsNoTracking()
				.Where(p => p.UserId == userID && p.Details != null)
				.Select(p => new { p.MatchId, p.Slot, p.Details })
				.ToListAsync(cancellationToken);

			int renamed = 0;
			foreach (var row in rows)
			{
				if (JsonNode.Parse(row.Details!) is not JsonObject details)
				{
					continue;
				}
				details["display_name"] = String.Empty;
				string json = details.ToJsonString();
				renamed += await db.MatchPlayers.Where(p => p.MatchId == row.MatchId && p.Slot == row.Slot)
					.ExecuteUpdateAsync(s => s.SetProperty(p => p.Details, json), cancellationToken);
			}
			return renamed;
		}

		// The sign-in provider links an erased account kept for its ban go once no ban on it is active
		public static Task<int> PurgeHeldIdentities(AppDbContext db, DateTime utcNow, CancellationToken cancellationToken = default)
		{
			IQueryable<Int64> banned = Database.Moderation.ActiveSanctions(db, SanctionTypes.Ban, utcNow).Select(s => s.UserID);
			IQueryable<Int64> erased = db.ErasedAccounts.Select(e => e.UserID);
			return db.Identities.Where(i => erased.Contains(i.UserID) && !banned.Contains(i.UserID)).ExecuteDeleteAsync(cancellationToken);
		}
	}
}
