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
	// mailbox and every sign-in provider id are kept only as keyed hashes (IdentifierHashes) so the same person cannot sign up
	// again unbanned, and the device rows stay without their raw value or address for ban-evasion matching;
	// IdentifierHashes.PurgeEndedHolds removes the hashes once no ban is active, the device purge the devices 90 days later.
	// Moderation records stay for their own retention: sanctions, their evidence, the staff log, reports, anti-cheat reports
	// and automated rule matches.
	public static class AccountErasure
	{
		public const string Action = "account_erase";

		// "Deleted" and six digits: never a name a player owes or chose, and it spreads over many names so tags never run out
		public static string TombstoneName() => "Deleted" + Random.Shared.Next(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

		private static readonly string s_tombstoneKey = Names.NameKey.Of("Deleted");
		private static readonly HashSet<string> s_digitKeys = Enumerable.Range(0, 10).Select(d => Names.NameKey.Of(d.ToString(CultureInfo.InvariantCulture))).ToHashSet(StringComparer.Ordinal);

		// The shape of TombstoneName, look-alikes included (De1eted, OOOOOO), which no live player may take as a name
		public static bool IsTombstoneShaped(string name)
		{
			return name.Length == 13
				&& String.Equals(Names.NameKey.Of(name.Substring(0, 7)), s_tombstoneKey, StringComparison.Ordinal)
				&& name.Substring(7).All(c => s_digitKeys.Contains(Names.NameKey.Of(c.ToString())));
		}

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
			// read before the row is cleared: a ban keeps its hash
			string? email = user.Email;

			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

			// the erased_accounts row is claimed first: a second erasure running at the same time waits on its key and then
			// fails on it, so only one erasure writes and audits
			db.ErasedAccounts.Add(new ErasedAccount { UserID = userID, ErasedAt = now });
			try
			{
				await db.SaveChangesAsync(cancellationToken);
			}
			catch (DbUpdateException ex) when (DbHelpers.IsUniqueViolation(ex))
			{
				await transaction.RollbackAsync(cancellationToken);
				db.ChangeTracker.Clear();
				return new ErasureResult(true, true, false, 0);
			}

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

			if (banned)
			{
				await HoldForBan(db, userID, email, now, cancellationToken);
			}
			else
			{
				await db.IdentifierHashes.Where(h => h.UserID == userID).ExecuteDeleteAsync(cancellationToken);
				await db.Devices.Where(d => d.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			}
			await db.Identities.Where(i => i.UserID == userID).ExecuteDeleteAsync(cancellationToken);

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

		// What a ban needs of an erased account, as hashes: the mailbox and each provider id, tied to the longest active ban.
		// The device rows stay for evasion matching (by value_hmac) but lose the raw value and the address.
		private static async Task HoldForBan(AppDbContext db, Int64 userID, string? email, DateTime now, CancellationToken cancellationToken)
		{
			Int64 banID = (await Database.Moderation.ActiveSanctions(db, SanctionTypes.Ban, now)
				.Where(s => s.UserID == userID)
				.OrderByDescending(s => s.ExpiresAt == null)
				.ThenByDescending(s => s.ExpiresAt)
				.FirstAsync(cancellationToken)).ID;

			await db.IdentifierHashes.Where(h => h.UserID == userID).ExecuteDeleteAsync(cancellationToken);
			if (IdentifierHashes.EmailHash(email) is byte[] mailbox)
			{
				db.IdentifierHashes.Add(new IdentifierHash { UserID = userID, Kind = HashedIdentifierKinds.Email, ValueHmac = mailbox, SanctionID = banID, CreatedAt = now });
			}

			List<UserIdentity> identities = await db.Identities.AsNoTracking().Where(i => i.UserID == userID).ToListAsync(cancellationToken);
			foreach (UserIdentity identity in identities)
			{
				string kind = HashedIdentifierKinds.Provider(identity.Provider);
				db.IdentifierHashes.Add(new IdentifierHash { UserID = userID, Kind = kind, ValueHmac = IdentifierHashes.Hash(kind, identity.ExternUid), SanctionID = banID, CreatedAt = now });
			}
			await db.SaveChangesAsync(cancellationToken);
			db.ChangeTracker.Clear();

			await db.Devices.Where(d => d.UserID == userID)
				.ExecuteUpdateAsync(s => s.SetProperty(d => d.ValueRaw, (string?)null).SetProperty(d => d.LastIp, (string?)null), cancellationToken);
		}
	}
}
